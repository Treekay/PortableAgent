import { eventNames, type ExecutionEvent } from './contracts';

export type StreamState = 'connecting' | 'open' | 'reconnecting' | 'closed' | 'unavailable' | 'protocol';
export interface StreamCallbacks { event: (event: ExecutionEvent) => void; state: (state: StreamState, message?: string) => void }
export type SourceFactory = (url: string) => EventSource;
export function validSequence(id: string): boolean {
  return /^(0|[1-9]\d*)$/.test(id) && BigInt(id) <= 9223372036854775807n;
}
export function parseEvent(message: MessageEvent, runId: string, namedType: string): ExecutionEvent {
  if (!validSequence(message.lastEventId)) throw new Error('Invalid SSE sequence.');
  const data = JSON.parse(message.data);
  if (!data || data.runId !== runId || typeof data.eventId !== 'string' || !data.eventId
    || typeof data.eventType !== 'string' || !data.eventType || !Number.isInteger(data.sequence)
    || typeof data.occurredAt !== 'string' || !Number.isFinite(Date.parse(data.occurredAt))
    || !data.payload || typeof data.payload !== 'object' || Array.isArray(data.payload)
    || (namedType !== 'message' && data.eventType !== namedType)
    || (Number.isSafeInteger(data.sequence) && BigInt(data.sequence) !== BigInt(message.lastEventId)))
    throw new Error('Invalid SSE event identity or content.');
  return { ...data, id: message.lastEventId };
}

export class EventStream {
  private source?: EventSource;
  private generation = 0;
  constructor(private factory: SourceFactory = url => new EventSource(url)) {}
  open(url: string, runId: string, after: string, callbacks: StreamCallbacks) {
    this.close();
    const generation = ++this.generation;
    const source = this.source = this.factory(`${url}?afterSequence=${encodeURIComponent(after)}`);
    const current = () => generation === this.generation && this.source === source;
    callbacks.state('connecting');
    source.onopen = () => { if (current()) callbacks.state('open'); };
    source.onerror = () => { if (current()) callbacks.state(source.readyState === 2 ? 'unavailable' : 'reconnecting'); };
    // Native EventSource has no wildcard for unknown named events. Unnamed future envelopes can still be retained.
    for (const name of [...eventNames, 'message']) source.addEventListener(name, raw => {
      if (!current()) return;
      let event: ExecutionEvent;
      try { event = parseEvent(raw as MessageEvent, runId, name); }
      catch {
        this.close();
        callbacks.state('protocol', 'Progress could not be read. Received history has been kept.');
        return;
      }
      callbacks.event(event);
    });
  }
  close() { ++this.generation; this.source?.close(); this.source = undefined; }
}
