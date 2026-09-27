import type { GraphFact } from './graphProjection';
export function GraphInspector({ node }: { node?: GraphFact }) {
  return <aside className="graph-inspector" aria-label="Selected node inspector" tabIndex={0}>
    <h3>{node ? node.title : 'Inspect a node'}</h3>{node ? <>
      <p>{node.kind} · {node.status}</p>{node.details.map((d, i) => <p key={i}>{d}</p>)}
      {node.kind === 'Tool Operation' && <p>{node.epoch === 'initial' ? 'Initial stage' : `Resume stage #${node.epoch}`} of this logical call. Status describes observations in this stage.</p>}
      <dl>{node.callId && <><dt>CallId</dt><dd>{node.callId}</dd></>}{node.toolName && <><dt>ToolName</dt><dd>{node.toolName}</dd></>}{node.approvalId && <><dt>ApprovalId</dt><dd>{node.approvalId}</dd></>}</dl>
      {node.events.map(({ event }) => <details key={event.id}><summary>#{event.id} · {event.eventType}</summary><pre aria-label={`Node payload #${event.id}`}>{JSON.stringify(event.payload, null, 2)}</pre></details>)}
    </> : <p>Select a node to read its observed status and related events. Trace provides the complete textual event list.</p>}
  </aside>;
}
