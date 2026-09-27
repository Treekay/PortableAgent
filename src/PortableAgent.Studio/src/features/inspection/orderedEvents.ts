import type { ExecutionEvent } from '../../api/contracts';
export const compareSequence = (a: string, b: string) => BigInt(a) < BigInt(b) ? -1 : BigInt(a) > BigInt(b) ? 1 : 0;
export function orderedEvents(events: readonly ExecutionEvent[]) {
  const unique = new Map<string, ExecutionEvent>();
  for (const event of events) if (!unique.has(event.id)) unique.set(event.id, event);
  return [...unique.values()].sort((a, b) => compareSequence(a.id, b.id));
}
