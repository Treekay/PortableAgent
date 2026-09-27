import type { ExecutionEvent } from '../../api/contracts';
export interface ExecutionGroup { key: string; title: string; detail: string; state: 'active' | 'done' | 'attention' | 'error' | 'muted' }
export function eventPresentation(events: ExecutionEvent[]): ExecutionGroup[] {
  const groups: ExecutionGroup[] = [];
  let epoch = 0;
  const update = (key: string, title: string, detail: string, state: ExecutionGroup['state']) => {
    const found = groups.find(g => g.key === key);
    if (found) Object.assign(found, { title, detail, state }); else groups.push({ key, title, detail, state });
  };
  for (const e of events) {
    const p = e.payload, call = String(p.callId ?? ''), op = `${epoch}:operation:${call}`, policy = `${epoch}:policy:${call}`;
    const tool = typeof p.toolName === 'string' ? p.toolName : groups.find(g => g.key.endsWith(`operation:${call}`))?.title ?? 'Tool operation';
    switch (e.eventType) {
      case 'RunResumed': ++epoch; groups.push({ key: e.id, title: 'Execution resumed', detail: 'Continuing the same run', state: 'done' }); break;
      case 'ToolDiscoveryStarted': update(`${epoch}:tools`, 'Tools', 'Discovering available tools', 'active'); break;
      case 'ToolDiscoveryCompleted': update(`${epoch}:tools`, 'Tools', `${p.toolCount} tools available`, 'done'); break;
      case 'ModelTurnStarted': update(`model:${p.turn}`, `Model turn ${p.turn}`, 'Awaiting model response', 'active'); break;
      case 'ModelTurnCompleted': update(`model:${p.turn}`, `Model turn ${p.turn}`, 'Model response received', 'done'); break;
      case 'ToolCallProposed': update(op, tool, 'Operation proposed', 'muted'); break;
      case 'PolicyEvaluationStarted': update(policy, 'Policy check', 'Evaluating this operation', 'active'); break;
      case 'PolicyEvaluationCompleted': update(policy, 'Policy check', p.outcome === 'RequireApproval' ? 'Requires approval' : String(p.outcome), p.outcome === 'Deny' ? 'attention' : 'done');
        if (p.outcome === 'Deny') update(op, tool, 'Blocked by policy', 'muted'); break;
      case 'ApprovalRequired': update(op, tool, 'Waiting for approval', 'attention'); break;
      case 'ApprovalResolved': update(op, tool, p.status === 'Approved' ? 'Approved' : 'Rejected', p.status === 'Approved' ? 'done' : 'muted'); break;
      case 'ToolExecutionStarted': update(op, tool, 'Executing', 'active'); break;
      case 'ToolExecutionCompleted': update(op, tool, p.success === true ? 'Completed' : 'Returned failure', p.success === true ? 'done' : 'error'); break;
      case 'RunCompleted': case 'RunFailed': case 'RunCancelled': case 'RunLimitReached':
        for (const group of groups) if (group.state === 'active') { group.state = 'muted'; group.detail = 'Run ended before a completion confirmation'; }
        break;
    }
  }
  return groups;
}
