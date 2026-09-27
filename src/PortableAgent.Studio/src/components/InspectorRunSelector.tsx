import type { StudioState } from '../features/run/runTypes';
export function InspectorRunSelector({ runs, value, onChange }: { runs: StudioState; value: string; onChange: (id: string) => void }) {
  return <label className="inspector-selector">Inspect Run<select aria-label="Inspect Run" value={value} onChange={e => onChange(e.target.value)}>
    {runs.filter(r => r.runId).map(r => <option key={r.clientId} value={r.clientId}>{r.agentName} · {r.userMessage.length > 65 ? r.userMessage.slice(0, 65) + '…' : r.userMessage} · {r.runtimeStatus ?? 'Starting'} · {r.runId!.slice(0, 8)}…</option>)}
  </select></label>;
}
