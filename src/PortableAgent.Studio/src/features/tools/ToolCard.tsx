import type { ToolCatalogEntryDto } from '../../api/contracts';
import { field } from '../inspection/inspectionTypes';
import type { ToolUsage } from './toolUsage';
import { ToolSchema } from './ToolSchema';
export function ToolCard({ tool, observations }: { tool: ToolCatalogEntryDto; observations: ToolUsage[] }) {
  return <article className="tool-card" aria-label={tool.modelName}>
    <span className="field-label">PERSISTED DEFINITION</span><h3><code>{tool.modelName}</code></h3>
    <p className="trusted-tool">Trusted ToolId <code>{tool.toolId.sourceId} / {tool.toolId.name}</code></p><p>{tool.description}</p>
    <ToolSchema schema={tool.inputSchema} />
    <div className="tool-observations"><h4>Observed usage in this Run</h4>{!observations.length ? <p>No associated usage observed. No policy evaluation observed.</p> : observations.map(({ call, association }) => <section key={call.callId}>
      <p><code>{call.callId}</code> · {association}</p>
      <p>Proposal observations: {call.proposals.length}</p>
      <h5>Observed policy in this Run</h5>
      {call.policyEvaluations.some(o => o.event.eventType === 'PolicyEvaluationCompleted') ? <ul>{call.policyEvaluations.filter(o => o.event.eventType === 'PolicyEvaluationCompleted').map(o => <li key={o.event.id}>#{o.event.id} · {field(o.event, 'outcome') ?? 'Not provided'}{field(o.event, 'policyId') ? ` · ${field(o.event, 'policyId')}` : ''}</li>)}</ul> : <p>No policy evaluation observed</p>}
      {call.approvalObservations.length > 0 && <ul aria-label="Approval observations">{call.approvalObservations.map(o => <li key={o.event.id}>#{o.event.id} · {o.event.eventType === 'ApprovalRequired' ? 'Approval required' : field(o.event, 'status') ?? 'Not provided'}</li>)}</ul>}
      <p>Execution starts observed: {call.executionStarts.length}</p><p>Execution completions observed: {call.executionCompletions.length}</p>
      <p>Returned failures: {call.executionCompletions.filter(o => o.event.payload.success === false).length}</p>
    </section>)}</div>
  </article>;
}
