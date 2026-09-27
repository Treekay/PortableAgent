import { useMemo, useRef } from 'react';
import { ReactFlow, ReactFlowProvider, Controls, ControlButton, MarkerType, useReactFlow, type Viewport } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { GraphNode, type FactNode } from './GraphNode';
import { graphLayout } from './graphLayout';
import type { GraphProjection } from './graphProjection';
const nodeTypes = { fact: GraphNode };
function FitControl() {
  const { fitView } = useReactFlow();
  return <Controls showFitView={false} showInteractive={false}><ControlButton aria-label="Fit graph" title="Fit graph" onClick={() => { void fitView({ duration: 0, maxZoom: 1 }); }}>⊡</ControlButton></Controls>;
}
interface Props { graph: GraphProjection; selected?: string; onSelect: (id: string) => void; viewport?: Viewport; onViewport: (viewport: Viewport) => void }
export function RunGraph(props: Props) { return <ReactFlowProvider><Canvas {...props} /></ReactFlowProvider>; }
function Canvas({ graph, selected, onSelect, viewport, onViewport }: Props) {
  const previous = useRef<FactNode[]>([]);
  const nodes = useMemo(() => {
    const positions = graphLayout(graph);
    const next: FactNode[] = graph.nodes.map(fact => {
      const node: FactNode = { id: fact.id, type: 'fact', data: { fact }, position: positions.get(fact.id)!, selected: selected === fact.id,
        draggable: false, connectable: false, deletable: false, ariaLabel: `${fact.kind}: ${fact.title}. ${fact.status}` };
      return previous.current.find(old => old.id === node.id && JSON.stringify(old) === JSON.stringify(node)) ?? node;
    }); previous.current = next; return next;
  }, [graph, selected]);
  const edges = useMemo(() => graph.edges.map(e => ({ ...e, type: 'smoothstep', animated: false, deletable: false, reconnectable: false,
    markerEnd: { type: MarkerType.ArrowClosed, color: e.meaning === 'relation' ? '#b8d7bd' : '#858e99' },
    ariaLabel: e.meaning === 'relation' ? 'Observed relationship' : 'Execution observation order',
    style: { stroke: e.meaning === 'relation' ? '#b8d7bd' : '#858e99', strokeDasharray: e.meaning === 'order' ? '5 5' : undefined } })), [graph]);
  return <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} colorMode="dark" nodesDraggable={false} nodesConnectable={false} edgesReconnectable={false}
    deleteKeyCode={null} fitView={!viewport} fitViewOptions={{ minZoom: .6, maxZoom: 1, padding: .15 }} minZoom={.08} maxZoom={1.7} defaultViewport={viewport}
    onMoveEnd={(_, view) => onViewport(view)} onNodeClick={(_, node) => onSelect(node.id)} onSelectionChange={({ nodes: selection }) => { if (selection[0]) onSelect(selection[0].id); }}
    ariaLabelConfig={{ 'node.a11yDescription.default': 'Press Enter to select this observation. Read related events in the selected node inspector.' }}>
    <FitControl />
  </ReactFlow>;
}
