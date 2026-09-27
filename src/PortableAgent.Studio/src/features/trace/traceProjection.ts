import type { Observation, RunInspection } from '../inspection/inspectionTypes';
import { field, numeric } from '../inspection/inspectionTypes';
export const traceFilters = ['All', 'Lifecycle', 'Model', 'Tools', 'Policy', 'Approval'] as const;
export type TraceFilter = typeof traceFilters[number];
const value = (item: unknown) => item === undefined || item === null ? 'Not provided' : String(item);
export function traceSummary({ event }: Observation): string {
  const text = (key: string) => value(field(event, key));
  switch (event.eventType) {
    case 'RunStarted': return 'Run started';
    case 'RunResumed': return 'Execution resumed in the same Run';
    case 'ToolDiscoveryStarted': return 'Tool discovery started';
    case 'ToolDiscoveryCompleted': return `${value(numeric(event, 'toolCount'))} tools observed during discovery`;
    case 'ModelTurnStarted': return `Model turn ${value(numeric(event, 'turn'))} started`;
    case 'ModelTurnCompleted': return `Model turn ${value(numeric(event, 'turn'))} · ${text('finishReason')} · ${value(numeric(event, 'toolCallCount'))} tool proposals`;
    case 'ToolCallProposed': return `${text('toolName')} · ${text('callId')}`;
    case 'PolicyEvaluationStarted': return `Policy evaluation · ${text('callId')}`;
    case 'PolicyEvaluationCompleted': return `${text('outcome')} · ${text('policyId')}`;
    case 'ApprovalRequired': return `Approval required · ${text('callId')}`;
    case 'ApprovalResolved': return `${text('status')} · ${text('callId')}`;
    case 'ToolExecutionStarted': return `${text('toolName')} · execution started`;
    case 'ToolExecutionCompleted': return `${text('toolName')} · ${event.payload.success === true ? 'success' : event.payload.success === false ? 'returned failure' : 'Not provided'}`;
    case 'RunCompleted': case 'RunFailed': case 'RunCancelled': case 'RunLimitReached':
      return `${text('status')} · ${value(numeric(event, 'modelTurns'))} model turns · ${value(numeric(event, 'toolCalls'))} tool calls`;
    default: return 'Unknown event type · raw evidence retained';
  }
}
export const traceProjection = (inspection: RunInspection, filter: TraceFilter) => inspection.orderedEvents
  .filter(e => filter === 'All' || e.category === filter).map(observation => ({ observation, summary: traceSummary(observation) }));
