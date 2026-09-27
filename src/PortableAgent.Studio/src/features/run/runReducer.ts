import type { AcceptedDto, ExecutionEvent, RunDto, RuntimeStatus } from '../../api/contracts';
import { terminal } from '../../api/contracts';
import type { StreamState } from '../../api/eventStream';
import type { CommandState, StudioRun, StudioState } from './runTypes';

export type Action =
  | { type: 'add'; run: StudioRun }
  | { type: 'accepted'; id: string; value: AcceptedDto }
  | { type: 'command'; id: string; command: 'startCommand' | 'approvalCommand' | 'cancelCommand'; value: CommandState }
  | { type: 'event'; id: string; event: ExecutionEvent }
  | { type: 'stream'; id: string; state: StreamState; message?: string }
  | { type: 'queryStart'; id: string; generation: number }
  | { type: 'snapshot'; id: string; generation: number; value: RunDto }
  | { type: 'queryError'; id: string; generation: number; message: string }
  | { type: 'expand'; id: string }
  | { type: 'release'; id: string };

export function newRun(clientId: string, agentId: string, agentName: string, userMessage: string): StudioRun {
  return { clientId, agentId, agentName, userMessage, startCommand: { state: 'submitting' }, stateSequenceId: '0',
    eventsBySequence: {}, orderedSequenceIds: [], lastSequenceId: '0', streamState: 'closed',
    approvalCommand: { state: 'idle' }, cancelCommand: { state: 'idle' }, queryState: 'idle', queryGeneration: 0,
    isExpanded: true, completionAutoCollapseApplied: false };
}
const eventStatus: Record<string, RuntimeStatus> = {
  RunStarted: 'Running', RunResumed: 'Running', ApprovalResolved: 'Running', ApprovalRequired: 'AwaitingApproval',
  RunCompleted: 'Completed', RunFailed: 'Failed', RunCancelled: 'Cancelled', RunLimitReached: 'LimitReached',
};
export function runReducer(state: StudioState, action: Action): StudioState {
  if (action.type === 'add') return [...state, action.run];
  return state.map(run => {
    if (run.clientId !== action.id) return run;
    switch (action.type) {
      case 'accepted': return { ...run, ...{ runId: action.value.runId, runUrl: action.value.runUrl, eventsUrl: action.value.eventsUrl }, startCommand: { state: 'accepted' } };
      case 'command':
        if (action.command === 'approvalCommand' && action.value.approvalId !== run.pendingApproval?.approvalId) return run;
        if (action.command === 'cancelCommand' && terminal(run.runtimeStatus)) return run;
        return { ...run, [action.command]: action.value };
      case 'stream': return { ...run, streamState: action.state, protocolError: action.message };
      case 'queryStart': return { ...run, queryGeneration: action.generation, queryState: 'loading', queryError: undefined };
      case 'queryError': return action.generation !== run.queryGeneration ? run : { ...run, queryState: 'error', queryError: action.message };
      case 'event': {
        const event = action.event;
        if (event.runId !== run.runId || run.eventsBySequence[event.id]) return run;
        const ids = [...run.orderedSequenceIds, event.id].sort((a, b) => BigInt(a) < BigInt(b) ? -1 : 1);
        const next = { ...run, eventsBySequence: { ...run.eventsBySequence, [event.id]: event }, orderedSequenceIds: ids, lastSequenceId: ids.at(-1)! };
        const status = eventStatus[event.eventType];
        if (!status || BigInt(event.id) <= BigInt(run.stateSequenceId) || terminal(run.runtimeStatus)) return next;
        const pendingMatches = status === 'AwaitingApproval' && event.payload.approvalId === run.pendingApproval?.approvalId;
        return { ...next, runtimeStatus: status, stateSequenceId: event.id,
          pendingApproval: pendingMatches ? run.pendingApproval : undefined,
          approvalCommand: pendingMatches ? run.approvalCommand : { state: 'idle' },
          isExpanded: status === 'Completed' ? run.isExpanded : true };
      }
      case 'snapshot': {
        const dto = action.value;
        if (action.generation !== run.queryGeneration || dto.runId !== run.runId) return run;
        const clean = { ...run, queryState: 'ready' as const, queryError: undefined };
        const seq = String(dto.snapshotSequence);
        if (!Number.isSafeInteger(dto.snapshotSequence) || dto.snapshotSequence < 0) return { ...clean, queryState: 'error', queryError: 'Invalid snapshot sequence.' };
        if (BigInt(seq) < BigInt(run.stateSequenceId) || (terminal(run.runtimeStatus) && dto.status !== run.runtimeStatus)) return clean;
        const approval = dto.status === 'AwaitingApproval' ? dto.pendingApproval ?? undefined : undefined;
        if (approval && Object.values(run.eventsBySequence).some(e => e.eventType === 'ApprovalResolved' && e.payload.approvalId === approval.approvalId)) return clean;
        const sameApproval = approval?.approvalId === run.pendingApproval?.approvalId;
        const complete = dto.status === 'Completed' && dto.finalText !== null;
        return { ...clean, runtimeStatus: dto.status, stateSequenceId: seq,
          snapshot: { sequence: seq, isActive: dto.isActive, modelTurns: dto.modelTurns, toolCalls: dto.toolCalls },
          pendingApproval: approval, approvalCommand: sameApproval ? run.approvalCommand : { state: 'idle' },
          finalText: complete ? dto.finalText! : undefined, failure: dto.status === 'Failed' ? dto.failure?.message ?? 'Run execution did not complete.' : undefined,
          completionAutoCollapseApplied: run.completionAutoCollapseApplied || complete,
          isExpanded: complete && !run.completionAutoCollapseApplied ? false : run.isExpanded };
      }
      case 'expand': return { ...run, isExpanded: !run.isExpanded };
      case 'release': return { ...run, released: true };
    }
  });
}
