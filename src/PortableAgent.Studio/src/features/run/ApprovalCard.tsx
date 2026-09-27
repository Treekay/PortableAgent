import { ShieldCheck } from 'lucide-react';
import type { ApprovalDto } from '../../api/contracts';
import type { CommandState } from './runTypes';
export function ApprovalCard({ approval, command, onDecision, onRefresh }: { approval: ApprovalDto; command: CommandState; onDecision: (decision: 'approve' | 'reject') => void; onRefresh: () => void }) {
  const disabled = ['submitting', 'accepted', 'unknown'].includes(command.state);
  return <section className="approval-card" aria-label="Approval required"><div className="approval-title"><ShieldCheck size={18} aria-hidden="true" /><h3>Approval required</h3></div>
    <p className="approval-tool"><code>{approval.toolName}</code></p>
    <p className="trusted-tool">Trusted tool <code>{approval.toolId.sourceId} / {approval.toolId.name}</code></p>
    <p>{approval.policyReason ?? 'Confirm this exact operation before it executes.'}</p>
    <span className="field-label">ARGUMENTS · READ ONLY</span><pre aria-label="Frozen approval arguments">{JSON.stringify(approval.arguments, null, 2)}</pre>
    <div className="approval-meta">Created {new Date(approval.createdAt).toLocaleString()}</div>
    {command.message && <p className="inline-error">{command.message}</p>}
    <div className="approval-actions"><span>{command.state === 'submitting' ? 'Submitting decision…' : command.state === 'accepted' ? 'Decision accepted · awaiting execution events' : 'Approve only the action shown above.'}</span>
      <button disabled={disabled} onClick={() => onDecision('reject')}>Reject</button><button className="primary" disabled={disabled} onClick={() => onDecision('approve')}>Approve</button></div>
    {command.state === 'unknown' && <button onClick={onRefresh}>Refresh approval status</button>}
  </section>;
}
