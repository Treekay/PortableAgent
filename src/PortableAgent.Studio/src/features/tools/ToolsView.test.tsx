import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { ToolsView } from './ToolsView';
import { runInspection } from '../inspection/runInspection';
import { catalog, flightEvents } from '../../test/inspectionFixtures';
it('separates trusted definitions, schema and observed policy/approval/counts', async () => {
  const user = userEvent.setup(), copy = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
  const dto = catalog(), events = flightEvents().map(e => e.eventType === 'ToolExecutionCompleted' ? { ...e, payload: { ...e.payload, success: false } } : e);
  render(<ToolsView query={{ state: 'ready', data: dto }} inspection={runInspection(events)} refresh={vi.fn()} />);
  const card = within(screen.getByRole('article', { name: 'cancel_booking' }));
  expect(card.getByText('flight-mcp / cancel_booking')).toBeVisible(); expect(card.getByText('Cancel a booking.')).toBeVisible();
  fireEvent.click(card.getByText('Input schema', { exact: true })); expect(card.getByLabelText('Input schema JSON').textContent).toBe(JSON.stringify(dto.tools[1].inputSchema, null, 2));
  await user.click(card.getByRole('button', { name: 'Copy schema' })); expect(copy).toHaveBeenCalledWith(JSON.stringify(dto.tools[1].inputSchema, null, 2));
  expect(card.getByText('Observed policy in this Run')).toBeVisible(); expect(card.getAllByText(/RequireApproval/)).toHaveLength(2); expect(card.getByText(/Approved/)).toBeVisible();
  expect(card.getByText('Execution starts observed: 1')).toBeVisible(); expect(card.getByText('Execution completions observed: 1')).toBeVisible(); expect(card.getByText('Returned failures: 1')).toBeVisible();
  expect(screen.getByText('No associated usage observed. No policy evaluation observed.')).toBeVisible();
  expect(screen.queryByRole('button', { name: /^(Run tool|Try tool|Execute|Test tool)$/i })).not.toBeInTheDocument();
});
it.each([
  ['Running', false, 'Tool catalog snapshot is not yet durably available.'],
  ['Running', true, 'This is the latest persisted catalog snapshot and may come from an earlier checkpoint.'],
  ['Failed', false, 'This persisted Run snapshot does not contain a tool catalog.'],
] as const)('describes %s catalog availability honestly (%s)', (status, hasTools, message) => {
  render(<ToolsView query={{ state: 'ready', data: catalog({ status, tools: hasTools ? catalog().tools : [] }) }} inspection={runInspection([])} refresh={vi.fn()} />);
  expect(screen.getByText(message)).toBeVisible(); expect(screen.queryByText('This Agent has no tools.')).not.toBeInTheDocument();
});
it('offers explicit refresh on query error and leaves stale snapshot labelled', () => {
  const refresh = vi.fn(); render(<ToolsView query={{ state: 'error', error: 'offline', data: catalog() }} inspection={runInspection([])} refresh={refresh} />);
  expect(screen.getByText(/offline/)).toBeVisible(); fireEvent.click(screen.getByRole('button', { name: 'Refresh catalog' })); expect(refresh).toHaveBeenCalledTimes(1);
});
