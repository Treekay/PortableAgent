import { describe, it, expect } from 'vitest';
import { newRun, runReducer, type Action } from './runReducer';
import { ack, approval, event, snapshot } from '../../test/fixtures';
import { controlsRun } from './runTypes';
const initial = () => runReducer([newRun('local', 'flight', 'Flight Booking', 'task')], { type: 'accepted', id: 'local', value: ack });
const apply = (state: ReturnType<typeof initial>, ...actions: Action[]) => actions.reduce(runReducer, state);
const ev = (id: string, type: string, payload = {}): Action => ({ type: 'event', id: 'local', event: event(id, type, payload) });
const query = (value: ReturnType<typeof snapshot>, generation = 1): Action[] => [{ type: 'queryStart', id: 'local', generation }, { type: 'snapshot', id: 'local', value, generation }];
describe('runReducer', () => {
  it('retains local user message before identity is bound', () => {
    const run = newRun('local', 'flight', 'Flight Booking', 'one independent task');
    expect(run.runId).toBeUndefined(); expect(run.startCommand.state).toBe('submitting'); expect(controlsRun(run)).toBe(true);
    expect(runReducer([run], { type: 'accepted', id: 'local', value: ack })[0]).toMatchObject({ runId: ack.runId, userMessage: run.userMessage });
  });
  it('deduplicates, preserves out of order unique ids and accepts gaps without losing Int64 precision', () => {
    const state = apply(initial(), ev('9007199254740993', 'ModelTurnStarted'), ev('12', 'ToolCallProposed'), ev('10', 'ModelTurnCompleted'), ev('12', 'RunFailed'));
    expect(state[0].orderedSequenceIds).toEqual(['10', '12', '9007199254740993']);
    expect(state[0].eventsBySequence['12'].eventType).toBe('ToolCallProposed'); expect(state[0].runtimeStatus).toBeUndefined();
  });
  it('does not regress a terminal state on a late snapshot or historical event', () => {
    const state = apply(initial(), ev('20', 'RunCompleted'), ...query(snapshot()), ev('9', 'ApprovalRequired', { approvalId: 'old' }));
    expect(state[0].runtimeStatus).toBe('Completed'); expect(state[0].pendingApproval).toBeUndefined();
  });
  it('does not restore a resolved approval from a stale GET', () => {
    const state = apply(initial(), ...query(snapshot({ status: 'AwaitingApproval', pendingApproval: approval, snapshotSequence: 9 })), ev('10', 'ApprovalResolved', { approvalId: approval.approvalId }),
      ...query(snapshot({ status: 'AwaitingApproval', pendingApproval: approval, snapshotSequence: 9 }), 2));
    expect(state[0].pendingApproval).toBeUndefined(); expect(state[0].runtimeStatus).toBe('Running');
  });
  it('ignores old query generations and approval-command responses', () => {
    const newer = { ...approval, approvalId: 'approval-2' };
    const state = apply(initial(), ...query(snapshot({ status: 'AwaitingApproval', pendingApproval: newer, snapshotSequence: 30 }), 2),
      { type: 'snapshot', id: 'local', generation: 1, value: snapshot({ status: 'AwaitingApproval', pendingApproval: approval, snapshotSequence: 9 }) },
      { type: 'command', id: 'local', command: 'approvalCommand', value: { state: 'accepted', approvalId: approval.approvalId } });
    expect(state[0].pendingApproval?.approvalId).toBe('approval-2'); expect(state[0].approvalCommand.state).toBe('idle');
  });
  it('collapses only after final GET and only once', () => {
    let state = apply(initial(), ev('20', 'RunCompleted'));
    expect(state[0].isExpanded).toBe(true); expect(state[0].finalText).toBeUndefined();
    const done = snapshot({ status: 'Completed', snapshotSequence: 20, finalText: 'Done.', isActive: false });
    state = apply(state, ...query(done)); expect(state[0].isExpanded).toBe(false);
    state = apply(state, { type: 'expand', id: 'local' }, ...query(done, 2)); expect(state[0].isExpanded).toBe(true);
  });
  it.each(['RunFailed', 'RunCancelled', 'RunLimitReached'])('keeps %s expanded', name => {
    expect(apply(initial(), ev('20', name))[0].isExpanded).toBe(true);
  });
  it('separates stream errors and tool failure from Run failure', () => {
    const state = apply(initial(), ev('1', 'RunStarted'), ev('12', 'ToolExecutionCompleted', { success: false }), { type: 'stream', id: 'local', state: 'reconnecting' });
    expect(state[0].runtimeStatus).toBe('Running'); expect(state[0].streamState).toBe('reconnecting');
  });
});
