import { field, numeric, type Observation, type RunInspection } from '../inspection/inspectionTypes';
import { compareSequence } from '../inspection/orderedEvents';
export type GraphKind = 'Run Start' | 'Tool Discovery' | 'Model Turn' | 'Tool Operation' | 'Approval' | 'Run Resume' | 'Terminal';
export interface GraphFact {
  id: string; kind: GraphKind; title: string; status: string; epoch: string; anchor: string;
  events: Observation[]; details: string[]; callId?: string; toolName?: string; approvalId?: string; modelKey?: string;
}
export interface GraphLink { id: string; source: string; target: string; meaning: 'relation' | 'order' }
export interface GraphProjection { nodes: GraphFact[]; edges: GraphLink[]; columns: string[][] }

export function graphProjection(inspection: RunInspection, runId: string, observationProblem = false): GraphProjection {
  const nodes: GraphFact[] = [], edges: GraphLink[] = [];
  const add = (kind: GraphKind, key: string, title: string, status: string, events: Observation[], extra: Partial<GraphFact> = {}) => {
    if (!events.length) return;
    nodes.push({ id: `${runId}:${kind}:${key}`, kind, title, status, epoch: events[0].epoch, anchor: events[0].event.id, events, details: [], ...extra });
  };
  const started = inspection.orderedEvents.find(o => o.event.eventType === 'RunStarted');
  if (started) add('Run Start', started.event.id, 'Run start', 'Observed', [started]);
  for (const group of inspection.discoveries) {
    const completed = group.events.find(o => o.event.eventType === 'ToolDiscoveryCompleted');
    add('Tool Discovery', group.key, 'Tool discovery', completed ? `${numeric(completed.event, 'toolCount') ?? 'Not provided'} tools observed` : 'Completion not observed', group.events);
  }
  for (const group of inspection.modelTurns) {
    const completed = group.events.find(o => o.event.eventType === 'ModelTurnCompleted');
    add('Model Turn', group.key, `Model turn ${numeric(group.events[0].event, 'turn') ?? 'Not provided'}`, completed ? 'Model response observed' : 'Awaiting model response', group.events, { modelKey: group.key });
  }
  for (const call of inspection.calls) {
    for (const epoch of inspection.epochs) {
      const all = call.events.filter(o => o.epoch === epoch.key);
      if (!all.length) continue;
      // Ambiguous proposals remain distinct; subsequent facts are not assigned to any one of them.
      const sets = call.ambiguous ? [ ...all.filter(o => o.event.eventType === 'ToolCallProposed').map(o => [o]), all.filter(o => o.event.eventType !== 'ToolCallProposed') ].filter(s => s.length) : [all];
      for (const events of sets) {
        const proposal = events.find(o => o.event.eventType === 'ToolCallProposed');
        const name = events.find(o => o.toolName)?.toolName ?? (!call.ambiguous ? call.proposals[0]?.toolName : undefined);
        const completion = events.filter(o => o.event.eventType === 'ToolExecutionCompleted').at(-1);
        const executionStart = events.some(o => o.event.eventType === 'ToolExecutionStarted');
        const policy = events.filter(o => o.event.eventType === 'PolicyEvaluationCompleted');
        const approval = events.filter(o => o.event.eventType === 'ApprovalResolved').at(-1);
        const waiting = events.some(o => o.event.eventType === 'ApprovalRequired') && !approval;
        const later = inspection.orderedEvents.some(o => compareSequence(o.event.id, events.at(-1)!.event.id) > 0 && (o.event.eventType === 'ModelTurnStarted' || o.event.eventType === 'RunResumed' || o === inspection.terminalEvent));
        const status = completion ? completion.event.payload.success === false ? 'Returned failure' : completion.event.payload.success === true ? 'Completed' : 'Completion result not provided'
          : executionStart ? 'Completion not observed'
          : policy.at(-1)?.event.payload.outcome === 'Deny' ? 'Blocked by policy'
          : waiting ? 'Waiting approval'
          : observationProblem || call.ambiguous ? 'Execution not observed'
          : later ? 'Not executed' : 'Execution not observed yet';
        const details = policy.map(p => `#${p.event.id} Policy: ${field(p.event, 'outcome') ?? 'Not provided'}`);
        if (approval) details.push(`Approval: ${field(approval.event, 'status') ?? 'Not provided'}`);
        if (call.ambiguous) details.push('Ambiguous CallId · proposal occurrences remain separate');
        if (status === 'Not executed') details.push('No ToolExecutionStarted event was observed.');
        add('Tool Operation', `${epoch.key}:${events[0].event.id}:${call.callId}`, name ?? 'Tool operation', status, events,
          { callId: call.callId, toolName: name, modelKey: proposal?.modelKey, details });
      }
    }
  }
  for (const approval of inspection.approvals) {
    const resolved = approval.events.find(o => o.event.eventType === 'ApprovalResolved');
    add('Approval', approval.key, 'Approval', resolved ? field(resolved.event, 'status') ?? 'Not provided' : 'Waiting approval', approval.events,
      { approvalId: approval.key, callId: approval.events[0].callId });
  }
  for (const o of inspection.orderedEvents.filter(o => o.event.eventType === 'RunResumed')) add('Run Resume', o.event.id, 'Run resumed', 'Same Run · new observation stage', [o]);
  if (inspection.terminalEvent) {
    const o = inspection.terminalEvent;
    add('Terminal', o.event.id, o.event.eventType.replace('Run', ''), field(o.event, 'status') ?? o.event.eventType.replace('Run', ''), [o]);
  }
  nodes.sort((a, b) => compareSequence(a.anchor, b.anchor));
  const link = (source: string, target: string, meaning: GraphLink['meaning']) => {
    if (source !== target && !edges.some(e => e.source === source && e.target === target)) edges.push({ id: `${source}->${target}`, source, target, meaning });
  };
  for (const node of nodes) {
    if (node.kind === 'Tool Operation' && node.modelKey) {
      const model = nodes.find(n => n.kind === 'Model Turn' && n.modelKey === node.modelKey);
      if (model) link(model.id, node.id, 'relation');
    }
    if (node.kind === 'Approval') {
      const candidates = nodes.filter(n => n.kind === 'Tool Operation' && n.callId === node.callId && n.epoch === node.epoch && n.events.some(e => e.approvalId === node.approvalId));
      if (candidates.length === 1 && !inspection.calls.find(c => c.callId === node.callId)?.ambiguous) link(candidates[0].id, node.id, 'relation');
    }
  }
  const groups: GraphFact[][] = [];
  for (const node of nodes) {
    const previous = groups.at(-1);
    if (node.kind === 'Tool Operation' && previous?.every(n => n.kind === 'Tool Operation' && n.epoch === node.epoch && n.modelKey === node.modelKey)) previous.push(node);
    else groups.push([node]);
  }
  for (let i = 1; i < groups.length; i++) for (const before of groups[i - 1]) for (const after of groups[i]) link(before.id, after.id, 'order');
  return { nodes, edges, columns: groups.map(group => group.map(n => n.id)) };
}
