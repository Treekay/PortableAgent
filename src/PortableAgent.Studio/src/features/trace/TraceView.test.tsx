import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it } from 'vitest';
import { TraceView } from './TraceView';
import { runInspection } from '../inspection/runInspection';
import { flightEvents } from '../../test/inspectionFixtures';
import { event } from '../../test/fixtures';
it('keyboard expansion retains exact payload and survives filtering and live updates', async () => {
  const events = flightEvents().slice(0, 9), inspection = runInspection(events);
  const { rerender } = render(<TraceView inspection={inspection} active />);
  const row = screen.getByRole('button', { name: /#6 / }); row.focus(); await userEvent.keyboard('{Enter}');
  expect(screen.getByLabelText('Payload #6').textContent).toBe(JSON.stringify(events[5].payload, null, 2));
  expect(screen.getByText(events[5].occurredAt)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Model' })); expect(screen.queryByLabelText('Payload #6')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'All' })); expect(screen.getByLabelText('Payload #6')).toBeVisible();
  rerender(<TraceView inspection={runInspection([...events, event('10', 'RunResumed'), event('10', 'RunResumed')])} active />);
  expect(screen.getAllByRole('button', { name: /#10 / })).toHaveLength(1); expect(screen.getByLabelText('Payload #6')).toBeVisible();
});
it('does not force a reader down on live updates and restores position after hidden updates', () => {
  const events = flightEvents().slice(0, 9), { container, rerender } = render(<TraceView inspection={runInspection(events)} active />);
  const area = container.querySelector('.trace-scroll')!;
  Object.defineProperties(area, { scrollHeight: { value: 2000, configurable: true }, clientHeight: { value: 400, configurable: true } });
  area.scrollTop = 100; fireEvent.scroll(area);
  rerender(<TraceView inspection={runInspection(flightEvents())} active />); expect(area.scrollTop).toBe(100);
  expect(screen.getByRole('button', { name: /New events/ })).toBeVisible();
  rerender(<TraceView inspection={runInspection(flightEvents())} active={false} />); area.scrollTop = 0; fireEvent.scroll(area);
  rerender(<TraceView inspection={runInspection(flightEvents())} active />); expect(area.scrollTop).toBe(100);
  act(() => fireEvent.click(screen.getByRole('button', { name: /New events/ }))); expect(area.scrollTop).toBe(2000);
});
