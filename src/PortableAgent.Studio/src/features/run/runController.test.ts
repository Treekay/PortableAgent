import { afterEach, describe, expect, it, vi } from 'vitest';
import { RunController } from './runController';
import { ApiError } from '../../api/portableAgentApi';
import { ack, approval, deferred, event, fakeApi, snapshot, sourceFactory } from '../../test/fixtures';
import type { AcceptedDto, RunDto } from '../../api/contracts';

const agent = { id: 'flight', name: 'API supplied name' };
const controllers: RunController[] = [];
function setup() {
  const api = fakeApi(), streams = sourceFactory(), controller = new RunController(api, streams.factory);
  controllers.push(controller); controller.activate();
  return { api, controller, streams, run: () => controller.getSnapshot()[0] };
}
const settle = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
afterEach(() => { controllers.forEach(c => c.suspend()); controllers.length = 0; vi.useRealTimers(); });

describe('run orchestration', () => {
  it('locks synchronously, creates the message before POST, then opens SSE before GET', async () => {
    const { api, controller, streams, run } = setup(), post = deferred<AcceptedDto>();
    vi.mocked(api.startRun).mockReturnValue(post.promise);
    vi.mocked(api.getRun).mockImplementation(async () => { expect(streams.sources).toHaveLength(1); return snapshot(); });
    const first = controller.start(agent, 'Original task');
    expect(run().userMessage).toBe('Original task'); expect(run().startCommand.state).toBe('submitting');
    expect(await controller.start(agent, 'Duplicate')).toBe(false);
    post.resolve(ack); expect(await first).toBe(true); await settle();
    expect(api.startRun).toHaveBeenCalledTimes(1); expect(run().runId).toBe(ack.runId);
    expect(api.startRun).toHaveBeenCalledWith('flight', 'Original task');
  });
  it('rejects blank/oversize input and never retries an unknown POST automatically', async () => {
    const { api, controller, run } = setup();
    expect(await controller.start(agent, ' ')).toBe(false);
    expect(await controller.start(agent, 'a'.repeat(8001))).toBe(false);
    vi.mocked(api.startRun).mockRejectedValue(new ApiError('offline', 'network'));
    await controller.start(agent, 'Keep me');
    expect(run().startCommand.state).toBe('unknown'); expect(run().userMessage).toBe('Keep me');
    expect(await controller.start(agent, 'Again')).toBe(false); expect(api.startRun).toHaveBeenCalledTimes(1);
    controller.releaseUnknown(run().clientId); await controller.start(agent, 'Explicit retry');
    expect(api.startRun).toHaveBeenCalledTimes(2);
  });
  it('coalesces GETs and rejects a stale initial response after newer lifecycle events', async () => {
    const { api, controller, streams, run } = setup(), initial = deferred<RunDto>();
    vi.mocked(api.getRun).mockReturnValueOnce(initial.promise).mockResolvedValue(snapshot({ snapshotSequence: 9, status: 'AwaitingApproval', pendingApproval: approval, isActive: false }));
    await controller.start(agent, 'Task');
    streams.sources[0].emit(event('9', 'ApprovalRequired', { approvalId: approval.approvalId }));
    void controller.refresh(run().clientId); expect(api.getRun).toHaveBeenCalledTimes(1);
    initial.resolve(snapshot()); await settle();
    expect(api.getRun).toHaveBeenCalledTimes(2); expect(run().pendingApproval?.approvalId).toBe(approval.approvalId);
  });
  it('does not turn historical approval replay into active controls after terminal GET', async () => {
    const { api, controller, streams, run } = setup();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'Completed', snapshotSequence: 20, finalText: 'Done' }));
    await controller.start(agent, 'Task'); await settle();
    streams.sources[0].emit(event('9', 'ApprovalRequired', { approvalId: approval.approvalId }));
    expect(run().pendingApproval).toBeUndefined(); expect(run().eventsBySequence['9']).toBeDefined();
    streams.sources[0].emit(event('20', 'RunCompleted')); expect(streams.sources[0].closed).toBe(true);
  });
  it('approval 202 waits for actual events and duplicate decisions are blocked', async () => {
    const { api, controller, run } = setup(), command = deferred<AcceptedDto>();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'AwaitingApproval', snapshotSequence: 9, pendingApproval: approval }));
    vi.mocked(api.submitApproval).mockReturnValue(command.promise);
    await controller.start(agent, 'Task'); await settle();
    const first = controller.approve(run().clientId, 'approve'); void controller.approve(run().clientId, 'reject');
    expect(run().approvalCommand.state).toBe('submitting'); expect(api.submitApproval).toHaveBeenCalledTimes(1);
    command.resolve(ack); await first;
    expect(run().approvalCommand.state).toBe('accepted'); expect(run().runtimeStatus).toBe('AwaitingApproval');
    expect(run().orderedSequenceIds).toEqual([]);
  });
  it('refreshes after 409 and recovers an uncertain approval only after a successful later query', async () => {
    const { api, controller, run } = setup();
    const paused = snapshot({ status: 'AwaitingApproval', snapshotSequence: 9, pendingApproval: approval });
    vi.mocked(api.getRun).mockResolvedValue(paused);
    await controller.start(agent, 'Task'); await settle();
    vi.mocked(api.submitApproval).mockRejectedValue(new ApiError('Conflict', 'http', 409));
    vi.mocked(api.getRun).mockRejectedValueOnce(new ApiError('offline', 'network'));
    await controller.approve(run().clientId, 'reject');
    expect(run().approvalCommand.state).toBe('unknown'); expect(run().queryState).toBe('error');
    await controller.refresh(run().clientId);
    expect(run().approvalCommand.state).toBe('rejected'); expect(api.submitApproval).toHaveBeenCalledTimes(1);
  });
  it('fences an old approval response from a second pending approval', async () => {
    const { api, controller, streams, run } = setup(), command = deferred<AcceptedDto>();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'AwaitingApproval', snapshotSequence: 9, pendingApproval: approval }));
    vi.mocked(api.submitApproval).mockReturnValue(command.promise);
    await controller.start(agent, 'Task'); await settle();
    const pending = controller.approve(run().clientId, 'approve');
    const second = { ...approval, approvalId: 'approval-2' };
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'AwaitingApproval', snapshotSequence: 19, pendingApproval: second }));
    streams.sources[0].emit(event('19', 'ApprovalRequired', { approvalId: second.approvalId })); await settle();
    command.resolve(ack); await pending;
    expect(run().pendingApproval?.approvalId).toBe(second.approvalId); expect(run().approvalCommand.state).toBe('idle');
  });
  it('cancel 202 remains Running until durable confirmation; 409 queries state', async () => {
    const { api, controller, run } = setup();
    await controller.start(agent, 'Task'); await settle(); await controller.cancel(run().clientId);
    expect(run().cancelCommand.state).toBe('accepted'); expect(run().runtimeStatus).toBe('Running');
    expect(api.cancelRun).toHaveBeenCalledTimes(1);
    const other = setup(); await other.controller.start(agent, 'Task'); await settle();
    vi.mocked(other.api.cancelRun).mockRejectedValue(new ApiError('Conflict', 'http', 409));
    await other.controller.cancel(other.run().clientId); await settle();
    expect(other.api.getRun).toHaveBeenCalledTimes(2);
  });
  it('retains timeline on final GET failure then collapses exactly once after Retry', async () => {
    const { api, controller, streams, run } = setup();
    await controller.start(agent, 'Task'); await settle();
    vi.mocked(api.getRun).mockRejectedValueOnce(new ApiError('offline', 'network'));
    streams.sources[0].emit(event('20', 'RunCompleted')); await settle();
    expect(run().runtimeStatus).toBe('Completed'); expect(run().isExpanded).toBe(true); expect(run().finalText).toBeUndefined();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'Completed', snapshotSequence: 20, finalText: 'Durable answer', modelTurns: 2, toolCalls: 1 }));
    await controller.refresh(run().clientId);
    expect(run().finalText).toBe('Durable answer'); expect(run().isExpanded).toBe(false);
    controller.expand(run().clientId); await controller.refresh(run().clientId); expect(run().isExpanded).toBe(true);
  });
  it('uses native reconnect only; cleanup fences callbacks and never sends cancel', async () => {
    vi.useFakeTimers(); const { api, controller, streams, run } = setup();
    await controller.start(agent, 'Task'); await settle(); streams.sources[0].error(); await settle();
    expect(run().streamState).toBe('reconnecting'); expect(run().runtimeStatus).toBe('Running');
    await vi.advanceTimersByTimeAsync(12000); expect(streams.sources).toHaveLength(1);
    controller.suspend(); expect(streams.sources[0].closed).toBe(true);
    controller.activate(); expect(streams.sources).toHaveLength(2);
    streams.sources[0].emit(event('99', 'RunFailed')); expect(run().runtimeStatus).toBe('Running');
    expect(api.cancelRun).not.toHaveBeenCalled();
  });
  it('preserves history on malformed SSE, queries durable state and manually resumes from the exact cursor', async () => {
    const { api, controller, streams, run } = setup();
    await controller.start(agent, 'Task'); await settle();
    streams.sources[0].emit(event('2', 'ToolDiscoveryStarted'));
    streams.sources[0].raw('ModelTurnStarted', '3', '{invalid'); await settle();
    expect(streams.sources[0].closed).toBe(true); expect(run().streamState).toBe('protocol');
    expect(run().runtimeStatus).toBe('Running'); expect(run().orderedSequenceIds).toEqual(['2']); expect(api.getRun).toHaveBeenCalledTimes(2);
    controller.reconnect(run().clientId); expect(streams.sources[1].url).toContain('afterSequence=2');
    expect(api.startRun).toHaveBeenCalledTimes(1);
  });
});
