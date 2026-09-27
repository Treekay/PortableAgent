import { describe, expect, it } from 'vitest';
import { runInspection } from './runInspection';
import { event } from '../../test/fixtures';
import { flightEvents } from '../../test/inspectionFixtures';
import { traceProjection, traceSummary } from '../trace/traceProjection';
describe('shared inspection facts', () => {
  it('sorts exact Int64 ids, accepts gaps, deduplicates and leaves raw payload untouched', () => {
    const raw = [event('9007199254740993', 'Future'), event('12', 'RunStarted'), event('2', 'ToolDiscoveryStarted'), event('12', 'RunFailed')];
    const before = JSON.stringify(raw), result = runInspection(raw);
    expect(result.orderedEvents.map(o => o.event.id)).toEqual(['2', '12', '9007199254740993']);
    expect(result.orderedEvents[1].event.eventType).toBe('RunStarted'); expect(JSON.stringify(raw)).toBe(before);
  });
  it('retains model turns, both policies and approval history across a single logical call', () => {
    const inspection = runInspection(flightEvents());
    expect(inspection.modelTurns).toHaveLength(2); expect(inspection.epochs).toHaveLength(2); expect(inspection.calls).toHaveLength(1);
    const call = inspection.calls[0]; expect(call.proposals).toHaveLength(1); expect(call.policyEvaluations).toHaveLength(4);
    expect(call.approvalObservations.map(o => o.event.eventType)).toEqual(['ApprovalRequired', 'ApprovalResolved']);
    expect(call.executionStarts).toHaveLength(1); expect(inspection.terminalEvent?.event.id).toBe('20');
  });
  it('preserves duplicate CallId proposals and diagnostics rather than choosing a valid proposal', () => {
    const inspection = runInspection([event('2', 'ToolCallProposed', { callId: 'x', toolName: 'a' }), event('3', 'ToolCallProposed', { callId: 'x', toolName: 'b' }), event('5', 'RunFailed')]);
    expect(inspection.calls[0].proposals).toHaveLength(2); expect(inspection.calls[0].ambiguous).toBe(true); expect(inspection.diagnostics[0]).toContain('#2, #3');
  });
  it('retains unknown and unassociated observations without inventing identities', () => {
    const result = runInspection([event('3', 'FutureEvent', { hidden: 'preserve as received' }), event('4', 'PolicyEvaluationCompleted', { outcome: 'Allow' })]);
    expect(result.unassociatedEvents).toHaveLength(2); expect(result.diagnostics).toHaveLength(2); expect(result.calls).toHaveLength(0); expect(result.terminalEvent).toBeUndefined();
  });
  it('does not associate proposals across a closed model proposal interval', () => {
    const inspection = runInspection([event('1', 'ModelTurnCompleted', { turn: 1 }), event('2', 'ToolDiscoveryStarted'), event('3', 'ToolCallProposed', { callId: 'c' })]);
    expect(inspection.calls[0].proposals[0].modelKey).toBeUndefined();
  });
  it.each([['All', 20], ['Lifecycle', 3], ['Model', 4], ['Tools', 7], ['Policy', 4], ['Approval', 2]] as const)('filters %s without combining rows', (filter, count) => {
    expect(traceProjection(runInspection(flightEvents()), filter)).toHaveLength(count);
  });
  it('unknown events appear in All only and summaries never invent missing fields', () => {
    const inspection = runInspection([event('1', 'FutureEvent'), event('2', 'ToolExecutionCompleted')]);
    expect(traceProjection(inspection, 'All')).toHaveLength(2); expect(traceProjection(inspection, 'Tools')).toHaveLength(1);
    expect(traceSummary(inspection.orderedEvents[1])).toBe('Not provided · Not provided');
    const policy = runInspection([event('9', 'PolicyEvaluationCompleted', { outcome: 'Deny', policyId: 'policy-id', callId: 'c' })]);
    expect(traceSummary(policy.orderedEvents[0])).toBe('Deny · policy-id');
  });
});
