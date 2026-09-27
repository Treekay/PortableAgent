import { vi } from 'vitest';
import type { AcceptedDto, ExecutionEvent, RunDto } from '../api/contracts';
import type { PortableAgentApi } from '../api/portableAgentApi';
export const runId = '11111111-1111-4111-8111-111111111111';
export const ack: AcceptedDto = { runId, status: 'accepted', runUrl: `/api/runs/${runId}`, eventsUrl: `/api/runs/${runId}/events` };
export const approval = { approvalId: 'approval-1', toolName: 'cancel_booking', toolId: { sourceId: 'flight-mcp', name: 'cancel_booking' }, arguments: { bookingId: 'NZ123' }, policyReason: 'Confirm this exact remote cancellation.', createdAt: '2026-09-27T08:00:00Z' };
export const snapshot = (values: Partial<RunDto> = {}): RunDto => ({ runId, agentId: 'flight', status: 'Running', modelTurns: 0, toolCalls: 0, snapshotSequence: 1, isActive: true, finalText: null, failure: null, pendingApproval: null, ...values });
export const event = (id: string, eventType: string, payload: Record<string, unknown> = {}): ExecutionEvent => ({ id, sequence: Number(id), eventId: `event-${id}`, runId, eventType, occurredAt: '2026-09-27T08:00:00Z', payload });
export const deferred = <T,>() => { let resolve!: (value: T) => void, reject!: (reason: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
export function fakeApi(): PortableAgentApi {
  return { listAgents: vi.fn().mockResolvedValue([{ id: 'flight', name: 'Flight Booking' }]), startRun: vi.fn().mockResolvedValue(ack),
    getRun: vi.fn().mockResolvedValue(snapshot()), submitApproval: vi.fn().mockResolvedValue({ ...ack, approvalId: approval.approvalId }), cancelRun: vi.fn().mockResolvedValue(ack) };
}
export class FakeSource {
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  readyState = 0;
  closed = false;
  listeners = new Map<string, EventListener>();
  constructor(public url: string) {}
  addEventListener(name: string, listener: EventListener) { this.listeners.set(name, listener); }
  close() { this.closed = true; this.readyState = 2; }
  open() { this.readyState = 1; this.onopen?.(); }
  error(closed = false) { this.readyState = closed ? 2 : 0; this.onerror?.(); }
  emit(value: ExecutionEvent, id = value.id) { this.listeners.get(value.eventType)?.(new MessageEvent(value.eventType, { lastEventId: id, data: JSON.stringify(value) })); }
  raw(name: string, id: string, data: string) { this.listeners.get(name)?.(new MessageEvent(name, { lastEventId: id, data })); }
}
export function sourceFactory() { const sources: FakeSource[] = []; return { sources, factory: (url: string) => { const source = new FakeSource(url); sources.push(source); return source as unknown as EventSource; } }; }
