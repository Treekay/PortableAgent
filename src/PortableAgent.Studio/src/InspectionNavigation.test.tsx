import { StrictMode } from 'react';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import App from './App';
import { portableAgentApi } from './api/portableAgentApi';
import { RunController } from './features/run/runController';
import { ack, approval, event, fakeApi, snapshot, sourceFactory } from './test/fixtures';
import { catalog } from './test/inspectionFixtures';
vi.mock('./features/graph/GraphView', () => ({ GraphView: () => <div>Graph projection test placeholder</div> }));
beforeEach(() => {
  vi.spyOn(portableAgentApi, 'listAgents').mockResolvedValue([{ id: 'flight', name: 'Flight Booking' }]);
  vi.spyOn(portableAgentApi, 'getRunTools').mockResolvedValue(catalog({ status: 'AwaitingApproval', snapshotSequence: 9 }));
});
const nav = (name: string) => fireEvent.click(within(screen.getByRole('navigation', { name: 'Studio views' })).getByRole('button', { name }));
async function setup() {
  const api = fakeApi(), streams = sourceFactory(), controller = new RunController(api, streams.factory);
  const view = render(<StrictMode><App runController={controller} /></StrictMode>);
  await screen.findByRole('option', { name: 'Flight Booking' });
  return { api, streams, controller, ...view };
}
it('empty inspectors use a bound Run only and have a visible way back to Chat', async () => {
  await setup(); nav('Trace'); expect(screen.getByText('Nothing to inspect yet.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Go to Chat' })); expect(screen.getByLabelText('Message')).toBeVisible();
});
it('all view switches preserve SSE, approval, draft, details, expansion and accumulated events', async () => {
  const { api, streams, controller, container } = await setup();
  vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'AwaitingApproval', snapshotSequence: 9, pendingApproval: approval }));
  await act(async () => { await controller.start({ id: 'flight', name: 'Flight Booking' }, 'Cancel my booking.'); });
  await screen.findByRole('button', { name: 'Approve' });
  fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Next draft' } });
  fireEvent.click(screen.getByText(/Show details/));
  const detail = container.querySelector('.execution-footer details')!; expect(detail).toHaveAttribute('open');
  const area = container.querySelector('.conversation-scroll')!; area.scrollTop = 123; fireEvent.scroll(area);
  for (const view of ['Trace', 'Graph', 'Tools']) { await act(async () => nav(view)); expect(streams.sources).toHaveLength(1); expect(streams.sources[0].closed).toBe(false); }
  await act(async () => streams.sources[0].emit(event('9', 'ApprovalRequired', { approvalId: approval.approvalId, callId: 'call-1' })));
  await act(async () => nav('Chat'));
  expect(screen.getByLabelText('Message')).toHaveValue('Next draft'); expect(screen.getByRole('button', { name: 'Approve' })).toBeEnabled();
  expect(detail).toHaveAttribute('open'); expect(area.scrollTop).toBe(123); expect(controller.getSnapshot()[0].eventsBySequence['9']).toBeDefined();
  expect(api.cancelRun).not.toHaveBeenCalled(); expect(controller.getSnapshot()[0].isExpanded).toBe(true);
});
it('selected older Run survives starting a newer Run and new lifecycle events', async () => {
  const { api, controller } = await setup();
  vi.mocked(api.getRun).mockResolvedValue(snapshot({ status: 'Completed', snapshotSequence: 20, finalText: 'Done.' }));
  await act(async () => { await controller.start({ id: 'flight', name: 'Flight Booking' }, 'First'); });
  nav('Trace'); const firstId = (screen.getByLabelText('Inspect Run') as HTMLSelectElement).value;
  nav('Chat'); const secondId = '22222222-2222-4222-8222-222222222222';
  vi.mocked(api.startRun).mockResolvedValue({ ...ack, runId: secondId, runUrl: `/api/runs/${secondId}`, eventsUrl: `/api/runs/${secondId}/events` });
  vi.mocked(api.getRun).mockResolvedValue(snapshot({ runId: secondId }));
  await act(async () => { await controller.start({ id: 'flight', name: 'Flight Booking' }, 'Second'); });
  nav('Tools'); expect(screen.getByLabelText('Inspect Run')).toHaveValue(firstId);
  expect(within(screen.getByLabelText('Inspect Run')).getAllByRole('option')).toHaveLength(2);
});
