import { memo } from 'react';
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react';
import { Circle, Check, Shield, AlertTriangle } from 'lucide-react';
import type { GraphFact } from './graphProjection';
export type FactNode = Node<{ fact: GraphFact }, 'fact'>;
export const GraphNode = memo(function GraphNode({ data, selected }: NodeProps<FactNode>) {
  const fact = data.fact;
  const Icon = fact.status === 'Completed' || fact.status === 'Approved' ? Check : /failure|Failed|Blocked/.test(fact.status) ? AlertTriangle : fact.kind === 'Approval' ? Shield : Circle;
  return <div className={`graph-fact nopan ${selected ? 'selected' : ''}`}>
    <Handle type="target" position={Position.Left} isConnectable={false} className="inspection-handle" />
    <span className="field-label">{fact.kind}</span><strong>{fact.title}</strong><span className="graph-status"><Icon size={14} />{fact.status}</span>
    {fact.kind === 'Tool Operation' && <small>{fact.epoch === 'initial' ? 'Initial proposal / execution stage' : `Resume stage #${fact.epoch}`} · same logical call</small>}
    {fact.details.slice(0, 2).map((detail, i) => <small key={i}>{detail}</small>)}<code>#{fact.anchor}{fact.callId ? ` · ${fact.callId}` : ''}</code>
    <Handle type="source" position={Position.Right} isConnectable={false} className="inspection-handle" />
  </div>;
});
