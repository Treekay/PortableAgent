import { useEffect, useState, useSyncExternalStore } from 'react';
import type { RunToolsDto } from '../../api/contracts';
import { errorText, portableAgentApi } from '../../api/portableAgentApi';
export interface ToolsQuery { state: 'idle' | 'loading' | 'ready' | 'error'; data?: RunToolsDto; error?: string }
const empty: ToolsQuery = { state: 'idle' };
interface Pending { abort: AbortController; generation: number; refreshNeeded: boolean }
export class ToolsQueryCache {
  private entries = new Map<string, ToolsQuery>();
  private checkpoints = new Map<string, string>();
  private pending = new Map<string, Pending>();
  private listeners = new Set<() => void>();
  private generation = 0;
  private active = false;
  constructor(private getTools = portableAgentApi.getRunTools) {}
  get = (id?: string) => id ? this.entries.get(id) ?? empty : empty;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private set(id: string, value: ToolsQuery) { this.entries.set(id, value); this.listeners.forEach(l => l()); }
  activate() { this.active = true; }
  suspend() { this.active = false; this.pending.forEach(p => p.abort.abort()); this.pending.clear(); this.checkpoints.clear(); }
  ensure(id: string, checkpoint: string) {
    if (this.checkpoints.get(id) !== checkpoint) { this.checkpoints.set(id, checkpoint); void this.refresh(id); }
  }
  async refresh(id: string): Promise<void> {
    if (!this.active) return;
    const current = this.pending.get(id);
    if (current) { current.refreshNeeded = true; return; }
    const pending = { abort: new AbortController(), generation: ++this.generation, refreshNeeded: false };
    this.pending.set(id, pending);
    this.set(id, { ...this.get(id), state: 'loading', error: undefined });
    try {
      const data = await this.getTools(id, pending.abort.signal);
      if (!this.active || this.pending.get(id)?.generation !== pending.generation) return;
      if (data.runId !== id || !Number.isSafeInteger(data.snapshotSequence) || data.snapshotSequence < 0 || !Array.isArray(data.tools)) throw new Error('Invalid tool catalog snapshot.');
      const old = this.get(id).data;
      this.set(id, { state: 'ready', data: old && old.snapshotSequence > data.snapshotSequence ? old : data });
    } catch (error) {
      if (!this.active || this.pending.get(id)?.generation !== pending.generation) return;
      this.set(id, { ...this.get(id), state: 'error', error: errorText(error) });
    } finally {
      if (this.pending.get(id)?.generation === pending.generation) {
        this.pending.delete(id);
        if (pending.refreshNeeded && this.active) void this.refresh(id);
      }
    }
  }
}
export function useRunTools(runId: string | undefined, checkpoint: string, enabled: boolean, provided?: ToolsQueryCache) {
  const [cache] = useState(() => provided ?? new ToolsQueryCache());
  const query = useSyncExternalStore(cache.subscribe, () => cache.get(runId));
  useEffect(() => { cache.activate(); return () => cache.suspend(); }, [cache]);
  useEffect(() => { if (enabled && runId) cache.ensure(runId, checkpoint); }, [cache, enabled, runId, checkpoint]);
  return { query, refresh: () => { if (runId) void cache.refresh(runId); } };
}
