import type { ReactNode } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { GraphView } from './GraphView';
import { runInspection } from '../inspection/runInspection';
import { flightEvents } from '../../test/inspectionFixtures';
import { runId } from '../../test/fixtures';
import type { FactNode } from './GraphNode';

interface FlowProps {
  nodes: FactNode[]; nodesDraggable: boolean; nodesConnectable: boolean; edgesReconnectable: boolean; deleteKeyCode: null;
  fitView: boolean; defaultViewport?: { x: number; y: number; zoom: number }; children: ReactNode;
  onNodeClick: (event: unknown, node: FactNode) => void;
  onMoveEnd: (event: unknown, viewport: { x: number; y: number; zoom: number }) => void;
}
const boundary = vi.hoisted(() => ({ props: undefined as FlowProps | undefined, fit: vi.fn() }));
vi.mock('@xyflow/react', async importOriginal => ({
  ...await importOriginal<typeof import('@xyflow/react')>(),
  ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  ReactFlow: (props: FlowProps) => { boundary.props = props; return <div>{props.nodes.map(node => <button key={node.id} onClick={() => props.onNodeClick(null, node)}>{node.data.fact.kind} {node.data.fact.anchor}</button>)}
    <button onClick={() => props.onMoveEnd(null, { x: 80, y: 40, zoom: .8 })}>Pan viewport</button>{props.children}</div>; },
  Controls: ({ children }: { children: ReactNode }) => <>{children}</>,
  ControlButton: ({ children, ...props }: React.ComponentProps<'button'>) => <button {...props}>{children}</button>,
  useReactFlow: () => ({ fitView: boundary.fit }),
}));
beforeEach(() => { boundary.props = undefined; boundary.fit.mockReset(); });
it('configures a read-only graph with explicit fit and textual selection', () => {
  render(<GraphView inspection={runInspection(flightEvents())} runId={runId} active observationProblem={false} />);
  expect(boundary.props).toMatchObject({ nodesDraggable: false, nodesConnectable: false, edgesReconnectable: false, deleteKeyCode: null });
  fireEvent.click(screen.getByRole('button', { name: 'Approval 9' })); expect(screen.getByText('Approval · Approved')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Fit graph' })); expect(boundary.fit).toHaveBeenCalledWith({ duration: 0, maxZoom: 1 });
});
it('live events retain unchanged node objects and never reset a user viewport', () => {
  const { rerender } = render(<GraphView inspection={runInspection(flightEvents().slice(0, 9))} runId={runId} active observationProblem={false} />);
  const initial = boundary.props!.nodes;
  fireEvent.click(screen.getByRole('button', { name: 'Pan viewport' }));
  rerender(<GraphView inspection={runInspection(flightEvents())} runId={runId} active observationProblem={false} />);
  expect(boundary.props!.nodes).toHaveLength(10); expect(new Set(boundary.props!.nodes.map(n => n.id)).size).toBe(10);
  expect(boundary.props!.nodes[0]).toBe(initial[0]); expect(boundary.props).toMatchObject({ fitView: false, defaultViewport: { x: 80, y: 40, zoom: .8 } });
  expect(boundary.fit).not.toHaveBeenCalled();
});
