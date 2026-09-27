import { StrictMode } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { ApiError, portableAgentApi } from './api/portableAgentApi';
import { RunController } from './features/run/runController';
import { Composer } from './features/run/Composer';
import { canCancel } from './features/run/runTypes';
import { newRun } from './features/run/runReducer';
import { ack, approval, deferred, event, fakeApi, snapshot, sourceFactory } from './test/fixtures';
import type { AcceptedDto } from './api/contracts';

beforeEach(() => { vi.spyOn(portableAgentApi, 'listAgents').mockResolvedValue([{ id: 'custom', name: 'Name from API' }]); });
function setup() {
  const api = fakeApi(), streams = sourceFactory(), controller = new RunController(api, streams.factory);
  const view = render(<StrictMode><App runController={controller} /></StrictMode>);
  return { api, streams, controller, ...view };
}
async function submit() {
  await screen.findByRole('option', { name: 'Name from API' });
  fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'My task' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send message' }));
}
describe('Studio controls', () => {
  it('renders a rejected start ProblemDetails message without inventing Run Failed', async () => {
    const { api } = setup(); vi.mocked(api.startRun).mockRejectedValue(new ApiError('Message must contain 1–8000 characters.', 'http', 400));
    await submit(); expect(await screen.findByText('Message must contain 1–8000 characters.')).toBeVisible();
    expect(screen.getByText('Could not start')).toBeVisible(); expect(screen.queryByText('Run execution did not complete.')).not.toBeInTheDocument();
  });
  it('retains the shell on API unavailability and Retry loads only API agent names', async () => {
    vi.mocked(portableAgentApi.listAgents).mockRejectedValue(new Error('offline'));
    setup(); expect(await screen.findByText('Cannot connect to PortableAgent API.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Send message' })).toBeDisabled();
    vi.mocked(portableAgentApi.listAgents).mockResolvedValue([{ id: 'custom', name: 'Recovered agent' }]);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('option', { name: 'Recovered agent' })).toHaveValue('custom');
  });
  it('renders immediate message/Starting, locks send and agent while leaving next draft editable', async () => {
    const { api } = setup(), post = deferred<AcceptedDto>(); vi.mocked(api.startRun).mockReturnValue(post.promise);
    await submit(); expect(screen.getByText('My task', { selector: 'p' })).toBeVisible(); expect(screen.getByText('Starting…')).toBeVisible();
    expect(screen.getByLabelText('Agent')).toBeDisabled(); expect(screen.getByLabelText('Message')).not.toBeDisabled();
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Next draft' } });
    await act(async () => post.resolve(ack)); expect(screen.getByLabelText('Message')).toHaveValue('Next draft');
    expect(api.startRun).toHaveBeenCalledTimes(1);
  });
  it('renders frozen approval arguments, disables both decisions immediately and does not fake resolution', async () => {
    const { api } = setup(), post = deferred<AcceptedDto>();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'AwaitingApproval', snapshotSequence: 9, pendingApproval: approval }));
    vi.mocked(api.submitApproval).mockReturnValue(post.promise);
    await submit(); const approve = await screen.findByRole('button', { name: 'Approve' });
    expect(screen.getByLabelText('Frozen approval arguments').textContent).toBe(JSON.stringify(approval.arguments, null, 2));
    expect(screen.getAllByRole('textbox')).toHaveLength(1); expect(screen.queryByRole('button', { name: 'Cancel run' })).not.toBeInTheDocument();
    fireEvent.click(approve); expect(approve).toBeDisabled(); expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled();
    await act(async () => post.resolve(ack)); expect(screen.getByText('Decision accepted · awaiting execution events')).toBeVisible();
    expect(api.submitApproval).toHaveBeenCalledWith(ack.runId, approval.approvalId, 'approve');
  });
  it('renders query failure Retry, durable answer below collapsed card, then respects expansion', async () => {
    const { api, streams, controller } = setup(); await submit(); await screen.findByText('Working');
    vi.mocked(api.getRun).mockRejectedValueOnce(new Error('Query unavailable'));
    await act(async () => streams.sources[0].emit(event('20', 'RunCompleted')));
    expect(await screen.findByText('Completed · final result unavailable')).toBeVisible();
    vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'Completed', snapshotSequence: 20, finalText: '<b>Plain durable answer</b>', modelTurns: 2, toolCalls: 1 }));
    fireEvent.click(screen.getByRole('button', { name: 'Retry result query' }));
    expect(await screen.findByText('<b>Plain durable answer</b>')).toBeVisible();
    const toggle = screen.getByRole('button', { name: /Completed.*2 model turns/ }); expect(toggle).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(toggle); await act(async () => controller.refresh(controller.getSnapshot()[0].clientId));
    expect(toggle).toHaveAttribute('aria-expanded', 'true'); expect(screen.getByText(/Show details/)).toBeVisible();
    expect(screen.getByLabelText('Agent')).not.toBeDisabled();
  });
  it.each([
    ['Failed', 'Run execution did not complete.'], ['Cancelled', 'Run cancelled.'], ['LimitReached', 'Execution stopped after reaching its configured limit.'],
  ] as const)('keeps %s expanded with specific copy', async (status, copy) => {
    const { api } = setup(); vi.mocked(api.getRun).mockResolvedValue(snapshot({ status, snapshotSequence: 20 }));
    await submit(); expect(await screen.findByText(copy)).toBeVisible();
    expect(screen.getByRole('button', { expanded: true })).toBeVisible();
  });
  it('cancel shows Cancelling without fabricating Cancelled and unmount only closes observation', async () => {
    const { api, streams, unmount } = setup(); await submit();
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel run' }));
    expect(await screen.findByText('Cancelling…')).toBeVisible(); expect(screen.queryByText('Run cancelled.')).not.toBeInTheDocument();
    unmount(); expect(streams.sources.every(s => s.closed)).toBe(true); expect(api.cancelRun).toHaveBeenCalledTimes(1);
  });
  it('shows new progress without pulling an upward-scrolled reader back to the bottom', async () => {
    const { streams, container } = setup(); await submit(); await screen.findByText('Working');
    const scroll = container.querySelector('.conversation-scroll')!;
    Object.defineProperties(scroll, { scrollHeight: { value: 1800, configurable: true }, clientHeight: { value: 400, configurable: true } });
    scroll.scrollTop = 0; fireEvent.scroll(scroll);
    await act(async () => streams.sources[0].emit(event('2', 'ToolDiscoveryStarted')));
    expect(scroll.scrollTop).toBe(0); fireEvent.click(screen.getByRole('button', { name: 'New progress' }));
    expect(scroll.scrollTop).toBe(1800);
  });
});
describe('composer and cancel evidence', () => {
  it('supports Enter, Shift+Enter and IME without accidental submit', async () => {
    const submit = vi.fn(), change = vi.fn();
    render(<Composer value="Task" onChange={change} onSubmit={submit} disabled={false} busy={false} />);
    const input = screen.getByLabelText('Message');
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true }); expect(submit).not.toHaveBeenCalled();
    fireEvent.compositionStart(input); fireEvent.keyDown(input, { key: 'Enter' }); expect(submit).not.toHaveBeenCalled();
    fireEvent.compositionEnd(input); fireEvent.keyDown(input, { key: 'Enter', isComposing: true }); expect(submit).not.toHaveBeenCalled();
    fireEvent.keyDown(input, { key: 'Enter' }); expect(submit).toHaveBeenCalledTimes(1);
    await userEvent.click(input); await userEvent.keyboard('{Shift>}{Enter}{/Shift}'); expect(change).toHaveBeenCalled();
  });
  it('enforces 8000 characters and blank input', () => {
    const props = { onChange: vi.fn(), onSubmit: vi.fn(), disabled: false, busy: false };
    const { rerender } = render(<Composer {...props} value={'a'.repeat(8000)} />);
    expect(screen.getByLabelText('Message')).toHaveAttribute('maxlength', '8000'); expect(screen.getByRole('button')).toBeEnabled();
    rerender(<Composer {...props} value={'a'.repeat(8001)} />); expect(screen.getByRole('button')).toBeDisabled();
    rerender(<Composer {...props} value="  " />); expect(screen.getByRole('button')).toBeDisabled();
  });
  it('requires active durable evidence without a newer lifecycle transition to allow cancel', () => {
    const run = { ...newRun('local', 'flight', 'Flight', 'Task'), runtimeStatus: 'Running' as const, snapshot: { sequence: '1', isActive: true, modelTurns: 0, toolCalls: 0 } };
    expect(canCancel(run)).toBe(true); expect(canCancel({ ...run, runtimeStatus: 'AwaitingApproval' })).toBe(false);
    expect(canCancel({ ...run, runtimeStatus: 'Completed' })).toBe(false); expect(canCancel({ ...run, stateSequenceId: '11' })).toBe(false);
    expect(canCancel({ ...run, snapshot: { ...run.snapshot, isActive: false } })).toBe(false);
  });
});
