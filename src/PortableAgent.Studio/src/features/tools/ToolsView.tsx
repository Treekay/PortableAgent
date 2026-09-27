import type { RunInspection } from '../inspection/inspectionTypes';
import type { ToolsQuery } from './useRunTools';
import { toolUsage } from './toolUsage';
import { ToolCard } from './ToolCard';
export function ToolsView({ query, inspection, refresh }: { query: ToolsQuery; inspection: RunInspection; refresh: () => void }) {
  const snapshot = query.data, usage = snapshot ? toolUsage(snapshot.tools, inspection) : undefined;
  return <div className="tools-view"><div className="tools-intro"><div><h2>Persisted tool catalog snapshot</h2><p>Definitions stored for this Run. This is not live MCP discovery or historical schema versioning.</p></div>
    <button disabled={query.state === 'loading'} onClick={refresh}>Refresh catalog</button></div>
    {query.state === 'loading' && <p role="status">Loading persisted catalog…</p>}{query.state === 'error' && <p className="inline-error">{query.error} Use Refresh catalog to retry.</p>}
    {snapshot && <><p className="snapshot-meta">Snapshot status: {snapshot.status} · snapshotSequence: {snapshot.snapshotSequence}</p>
      {snapshot.status === 'Running' && <p className="snapshot-notice">{snapshot.tools.length === 0 ? 'Tool catalog snapshot is not yet durably available.' : 'This is the latest persisted catalog snapshot and may come from an earlier checkpoint.'}</p>}
      {snapshot.status !== 'Running' && snapshot.tools.length === 0 && <p>This persisted Run snapshot does not contain a tool catalog.</p>}
      {snapshot.tools.length > 0 && <p>{snapshot.tools.length} trusted tools in this persisted snapshot. The schema shown is not proof of the schema used by every historical call.</p>}
      {usage?.entries.map(({ tool, observations }) => <ToolCard key={JSON.stringify(tool.toolId)} tool={tool} observations={observations} />)}
      {!!usage?.unassociated.length && <section className="unassociated"><h3>Unassociated observations</h3>{usage.unassociated.map(({ call, reason }) => <p key={call.callId}><code>{call.callId}</code> · {reason} · Sequences {call.events.map(o => `#${o.event.id}`).join(', ')}</p>)}</section>}
    </>}
  </div>;
}
