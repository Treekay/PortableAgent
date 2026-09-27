import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { GraphInspector } from './GraphInspector';
import { graphProjection } from './graphProjection';
import { runInspection } from '../inspection/runInspection';
import { flightEvents } from '../../test/inspectionFixtures';
import { runId } from '../../test/fixtures';
it('provides keyboard-reachable textual identity, status and exact related events', () => {
  const node = graphProjection(runInspection(flightEvents()), runId).nodes.find(n => n.kind === 'Approval')!;
  render(<GraphInspector node={node} />);
  expect(screen.getByRole('complementary')).toHaveAttribute('tabindex', '0'); expect(screen.getByText('Approval · Approved')).toBeVisible();
  expect(screen.getByText('approval-1')).toBeVisible(); fireEvent.click(screen.getByText('#10 · ApprovalResolved'));
  expect(screen.getByLabelText('Node payload #10').textContent).toBe(JSON.stringify(node.events[1].event.payload, null, 2));
});
