import { useState } from 'react';
import { Check, ChevronDown, ChevronRight, Copy, LoaderCircle, TriangleAlert, Square } from 'lucide-react';
import { terminal } from '../../api/contracts';
import { canCancel, type StudioRun } from './runTypes';
import type { RunController } from './runController';
import { eventPresentation } from './eventPresentation';
import { ExecutionStep } from './ExecutionStep';
import { ApprovalCard } from './ApprovalCard';

export function ExecutionCard({ run, controller }: { run: StudioRun; controller: RunController }) {
  const [copied, setCopied] = useState(false);
  const events = run.orderedSequenceIds.map(id => run.eventsBySequence[id]);
  const groups = eventPresentation(events);
  const status = run.runtimeStatus;
  const label = run.startCommand.state === 'unknown' ? 'Start unconfirmed' : run.startCommand.state === 'rejected' ? 'Could not start'
    : !status ? 'Starting…' : status === 'Running' ? 'Working' : status === 'AwaitingApproval' ? 'Approval required' : status === 'LimitReached' ? 'Limit reached' : status;
  const Icon = status === 'Completed' ? Check : ['Failed', 'LimitReached'].includes(status ?? '') ? TriangleAlert : status === 'Cancelled' ? Square : LoaderCircle;
  const progress = run.streamState === 'reconnecting' ? 'Reconnecting…' : run.cancelCommand.state === 'submitting' ? 'Submitting cancellation…'
    : run.cancelCommand.state === 'accepted' && !terminal(status) ? 'Cancelling…' : undefined;
  const summary = status === 'Completed' && run.finalText !== undefined && run.snapshot
    ? `${run.snapshot.modelTurns} model turns · ${run.snapshot.toolCalls} tool ${run.snapshot.toolCalls === 1 ? 'call' : 'calls'}`
    : progress ?? run.agentName;
  return <section className={`execution-card ${status === 'AwaitingApproval' ? 'awaiting' : ''}`} aria-label="Execution">
    <button className="execution-toggle" aria-expanded={run.isExpanded} onClick={() => controller.expand(run.clientId)}>
      <span className={`status-icon ${status ?? 'Starting'}`}><Icon size={17} aria-hidden="true" className={!terminal(status) && status !== 'AwaitingApproval' && run.startCommand.state === 'accepted' ? 'spinner' : ''} /></span>
      <span className="execution-heading"><strong>{label}</strong><span>{summary}</span></span>
      {run.isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
    </button>
    {run.isExpanded && <div className="execution-content">
      {groups.length > 0 && <ol className="execution-steps">{groups.map(group => <ExecutionStep key={group.key} group={group} />)}</ol>}
      {run.startCommand.message && <p className="inline-error">{run.startCommand.message}</p>}
      {run.startCommand.state === 'unknown' && !run.released && <button onClick={() => controller.releaseUnknown(run.clientId)}>I understand · allow a new task</button>}
      {status === 'AwaitingApproval' && (run.pendingApproval
        ? <ApprovalCard approval={run.pendingApproval} command={run.approvalCommand} onDecision={decision => { void controller.approve(run.clientId, decision); }} onRefresh={() => { void controller.refresh(run.clientId); }} />
        : <p className="muted">Loading approval…</p>)}
      {status === 'Failed' && <p className="inline-error">{run.failure ?? 'Run execution did not complete.'}</p>}
      {status === 'Cancelled' && <p>Run cancelled.</p>}
      {status === 'LimitReached' && <p>Execution stopped after reaching its configured limit.</p>}
      {status === 'Completed' && run.finalText === undefined && <p>Completed · {run.queryState === 'loading' ? 'fetching final result…' : 'final result unavailable'}</p>}
      {run.queryState === 'error' && <div className="error-with-action"><span>{run.queryError}</span><button onClick={() => { void controller.refresh(run.clientId); }}>Retry result query</button></div>}
      {['protocol', 'unavailable'].includes(run.streamState) && !terminal(status) && <div className="error-with-action"><span>{run.protocolError ?? 'Progress stream unavailable. The run may still be executing.'}</span><button onClick={() => controller.reconnect(run.clientId)}>Reconnect progress</button></div>}
      {status === 'Running' && run.snapshot?.isActive === false && <p className="muted">No active execution is reported by this API host. Automatic recovery is unavailable.</p>}
      {run.cancelCommand.message && <p className="inline-error">{run.cancelCommand.message}</p>}
      {canCancel(run) && <button className="subtle-button" onClick={() => { void controller.cancel(run.clientId); }}><Square size={12} /> Cancel run</button>}
      {run.runId && <div className="execution-footer"><details><summary>Show details <span>{events.length} events</span></summary>
        {events.map(event => <div className="raw-event" key={event.id}><div><code>{event.id} · {event.eventType}</code><time>{new Date(event.occurredAt).toLocaleTimeString()}</time></div><pre>{JSON.stringify(event.payload, null, 2)}</pre></div>)}
      </details><button className="copy-id" title={run.runId} onClick={() => { void navigator.clipboard.writeText(run.runId!).then(() => setCopied(true)).catch(() => setCopied(false)); }}><Copy size={12} />{copied ? 'Copied' : `Run ${run.runId.slice(0, 8)}…`}</button></div>}
    </div>}
  </section>;
}
