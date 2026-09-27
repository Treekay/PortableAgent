import type { ExecutionEvent } from '../../api/contracts';
export type StudioView = 'chat' | 'trace' | 'graph' | 'tools';
export type Category = 'Lifecycle' | 'Model' | 'Tools' | 'Policy' | 'Approval' | 'Unknown';
export interface Observation {
  event: ExecutionEvent; epoch: string; category: Category;
  callId?: string; toolName?: string; approvalId?: string; modelKey?: string;
}
export interface EventGroup { key: string; epoch: string; events: Observation[] }
export interface CallInspection {
  callId: string; proposals: Observation[]; policyEvaluations: Observation[];
  approvalObservations: Observation[]; executionStarts: Observation[]; executionCompletions: Observation[];
  events: Observation[]; ambiguous: boolean;
}
export interface RunInspection {
  orderedEvents: Observation[]; epochs: EventGroup[]; modelTurns: EventGroup[]; discoveries: EventGroup[];
  calls: CallInspection[]; approvals: EventGroup[]; terminalEvent?: Observation;
  unassociatedEvents: Observation[]; diagnostics: string[];
}
export const field = (event: ExecutionEvent, name: string) => typeof event.payload[name] === 'string' ? event.payload[name] as string : undefined;
export const numeric = (event: ExecutionEvent, name: string) => typeof event.payload[name] === 'number' ? event.payload[name] as number : undefined;
