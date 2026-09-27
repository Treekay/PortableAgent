import { lazy, Suspense, useMemo } from 'react';
import type { RunInspection, StudioView } from './inspectionTypes';
import type { StudioRun, StudioState } from '../run/runTypes';
import { terminal } from '../../api/contracts';
import { InspectorRunSelector } from '../../components/InspectorRunSelector';
import { runInspection } from './runInspection';
import { TraceView } from '../trace/TraceView';
import { ToolsView } from '../tools/ToolsView';
import { useRunTools } from '../tools/useRunTools';
const GraphView = lazy(() => import('../graph/GraphView').then(module => ({ default: module.GraphView })));
export function InspectorWorkspace({ runs, selected, onSelect, view, goChat }: { runs: StudioState; selected: string | null; onSelect: (id: string) => void; view: StudioView; goChat: () => void }) {
  const run = runs.find(r => r.clientId === selected && r.runId);
  const inspection = useMemo(() => runInspection(Object.values(run?.eventsBySequence ?? {})), [run?.eventsBySequence]);
  const { query, refresh } = useRunTools(run?.runId, `${run?.runtimeStatus}:${run?.snapshot?.sequence}`, view === 'tools');
  return <section className="inspector-workspace" hidden={view === 'chat'} aria-label="Run inspection">
    {!run ? <div className="inspector-empty"><h2>Nothing to inspect yet.</h2><p>Run a task in Chat to inspect its trace, graph and tools.</p><button onClick={goChat}>Go to Chat</button></div> : <>
      <div className="inspector-heading"><div><span className="eyebrow">RUN INSPECTION</span><h2>{view === 'graph' ? 'Run graph' : view === 'tools' ? 'Tools' : 'Trace'}</h2></div><InspectorRunSelector runs={runs} value={run.clientId} onChange={onSelect} /></div>
      <SelectedRun key={run.clientId} run={run} view={view} inspection={inspection} tools={<ToolsView query={query} refresh={refresh} inspection={inspection} />} />
    </>}
  </section>;
}
function SelectedRun({ run, view, tools, inspection }: { run: StudioRun; view: StudioView; tools: React.ReactNode; inspection: RunInspection }) {
  const problem = ['protocol', 'unavailable', 'reconnecting'].includes(run.streamState) || !inspection.orderedEvents.some(o => o.event.eventType === 'RunStarted')
    || (terminal(run.runtimeStatus) && !inspection.terminalEvent);
  return <><div className="inspection-status"><span>Runtime: {run.runtimeStatus ?? 'Starting'}</span><span>Stream: {run.streamState}</span><span>{inspection.orderedEvents.length} events</span><code>Run {run.runId!.slice(0, 8)}…</code></div>
    {terminal(run.runtimeStatus) && !inspection.terminalEvent && <p className="snapshot-notice">Runtime status is {run.runtimeStatus}; a terminal event has not yet been observed.</p>}
    {problem && <p className="snapshot-notice">Observation may be incomplete. Missing events are not evidence of a business outcome.</p>}
    {!!inspection.diagnostics.length && <details className="inspection-diagnostics"><summary>{inspection.diagnostics.length} observation diagnostics</summary>{inspection.diagnostics.map(d => <p key={d}>{d}</p>)}</details>}
    <div className="inspection-page" hidden={view !== 'trace'}><TraceView inspection={inspection} active={view === 'trace'} /></div>
    <div className="inspection-page" hidden={view !== 'graph'}><Suspense fallback={<p>Loading graph…</p>}><GraphView inspection={inspection} runId={run.runId!} active={view === 'graph'} observationProblem={problem} /></Suspense></div>
    <div className="inspection-page tools-page" hidden={view !== 'tools'}>{tools}</div>
  </>;
}
