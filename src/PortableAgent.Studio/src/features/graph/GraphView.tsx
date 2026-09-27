import { useMemo, useState } from 'react';
import type { Viewport } from '@xyflow/react';
import type { RunInspection } from '../inspection/inspectionTypes';
import { graphProjection } from './graphProjection';
import { RunGraph } from './RunGraph';
import { GraphInspector } from './GraphInspector';
export function GraphView({ inspection, runId, active, observationProblem }: { inspection: RunInspection; runId: string; active: boolean; observationProblem: boolean }) {
  const graph = useMemo(() => graphProjection(inspection, runId, observationProblem), [inspection, runId, observationProblem]);
  const [selected, setSelected] = useState<string>(), [viewport, setViewport] = useState<Viewport>();
  return <div className="graph-view"><div className="graph-legend"><span>━━ Solid = observed relationship</span><span>┄┄ Dashed = execution observation order, not data dependency</span></div>
    <div className="graph-body"><div className="graph-canvas" aria-label="Run graph">{active && graph.nodes.length > 0 ? <RunGraph graph={graph} selected={selected} onSelect={setSelected} viewport={viewport} onViewport={setViewport} /> : <p className="muted">No graph observations yet.</p>}</div>
      <GraphInspector node={graph.nodes.find(n => n.id === selected)} /></div>
  </div>;
}
