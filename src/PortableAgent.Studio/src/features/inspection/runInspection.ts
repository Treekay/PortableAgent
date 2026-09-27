import { eventNames, type ExecutionEvent } from '../../api/contracts';
import { orderedEvents } from './orderedEvents';
import { callIndex } from './callIndex';
import { field, numeric, type Category, type EventGroup, type Observation, type RunInspection } from './inspectionTypes';

const known = new Set<string>(eventNames);
function category(type: string): Category {
  if (!known.has(type)) return 'Unknown';
  if (type.startsWith('Run')) return 'Lifecycle';
  if (type.startsWith('Model')) return 'Model';
  if (type.startsWith('Policy')) return 'Policy';
  if (type.startsWith('Approval')) return 'Approval';
  return 'Tools';
}
export function runInspection(raw: readonly ExecutionEvent[]): RunInspection {
  const result: RunInspection = { orderedEvents: [], epochs: [], modelTurns: [], discoveries: [], calls: [], approvals: [], unassociatedEvents: [], diagnostics: [] };
  let epoch = 'initial', model: EventGroup | undefined, discovery: EventGroup | undefined, proposalModel: string | undefined;
  const approvals = new Map<string, EventGroup>();
  for (const event of orderedEvents(raw)) {
    const type = event.eventType;
    if (type === 'RunResumed') { epoch = event.id; model = undefined; discovery = undefined; }
    if (type !== 'ToolCallProposed' && type !== 'ModelTurnStarted' && type !== 'ModelTurnCompleted') proposalModel = undefined;
    const observation: Observation = { event, epoch, category: category(type), callId: field(event, 'callId'), toolName: field(event, 'toolName'), approvalId: field(event, 'approvalId') };
    result.orderedEvents.push(observation);
    let phase = result.epochs.find(e => e.key === epoch);
    if (!phase) { phase = { key: epoch, epoch, events: [] }; result.epochs.push(phase); }
    phase.events.push(observation);
    if (type === 'ModelTurnStarted' || type === 'ModelTurnCompleted') {
      if (type === 'ModelTurnStarted' || !model || numeric(model.events[0].event, 'turn') !== numeric(event, 'turn')) {
        model = { key: `model:${event.id}`, epoch, events: [] }; result.modelTurns.push(model);
      }
      model.events.push(observation); observation.modelKey = model.key; proposalModel = model.key;
    }
    if (type === 'ToolCallProposed') observation.modelKey = proposalModel;
    if (type === 'ToolDiscoveryStarted' || type === 'ToolDiscoveryCompleted') {
      if (type === 'ToolDiscoveryStarted' || !discovery) { discovery = { key: `discovery:${event.id}`, epoch, events: [] }; result.discoveries.push(discovery); }
      discovery.events.push(observation);
    }
    if (observation.category === 'Approval' && observation.approvalId) {
      let approval = approvals.get(observation.approvalId);
      if (!approval) { approval = { key: observation.approvalId, epoch, events: [] }; approvals.set(approval.key, approval); result.approvals.push(approval); }
      approval.events.push(observation);
    }
    if (['RunCompleted', 'RunFailed', 'RunCancelled', 'RunLimitReached'].includes(type)) result.terminalEvent = observation;
    const needsCall = ['ToolCallProposed', 'PolicyEvaluationStarted', 'PolicyEvaluationCompleted', 'ApprovalRequired', 'ApprovalResolved', 'ToolExecutionStarted', 'ToolExecutionCompleted'].includes(type);
    if (observation.category === 'Unknown' || (needsCall && !observation.callId) || (observation.category === 'Approval' && !observation.approvalId)) {
      result.unassociatedEvents.push(observation);
      result.diagnostics.push(`#${event.id} ${type}: ${observation.category === 'Unknown' ? 'unknown event retained' : 'association identity not provided'}.`);
    }
  }
  result.calls = callIndex(result.orderedEvents);
  for (const call of result.calls) {
    if (call.ambiguous) result.diagnostics.push(`Call ${call.callId}: multiple proposals at ${call.proposals.map(p => `#${p.event.id}`).join(', ')}; association is ambiguous.`);
    if (!call.proposals.length) result.diagnostics.push(`Call ${call.callId}: no proposal observed; related events retained.`);
  }
  return result;
}
