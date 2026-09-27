import type { CallInspection, Observation } from './inspectionTypes';
export function callIndex(events: Observation[]): CallInspection[] {
  const calls = new Map<string, CallInspection>();
  for (const observation of events) {
    const { callId, event } = observation;
    if (!callId) continue;
    let call = calls.get(callId);
    if (!call) { call = { callId, proposals: [], policyEvaluations: [], approvalObservations: [], executionStarts: [], executionCompletions: [], events: [], ambiguous: false }; calls.set(callId, call); }
    call.events.push(observation);
    if (event.eventType === 'ToolCallProposed') call.proposals.push(observation);
    if (event.eventType.startsWith('PolicyEvaluation')) call.policyEvaluations.push(observation);
    if (event.eventType.startsWith('Approval')) call.approvalObservations.push(observation);
    if (event.eventType === 'ToolExecutionStarted') call.executionStarts.push(observation);
    if (event.eventType === 'ToolExecutionCompleted') call.executionCompletions.push(observation);
  }
  for (const call of calls.values()) call.ambiguous = call.proposals.length > 1;
  return [...calls.values()];
}
