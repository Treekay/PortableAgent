import type { ApprovalDto, ExecutionEvent, RuntimeStatus } from '../../api/contracts';
import { terminal } from '../../api/contracts';
import type { StreamState } from '../../api/eventStream';

export interface CommandState { state: 'idle' | 'submitting' | 'accepted' | 'rejected' | 'unknown'; message?: string; approvalId?: string }
export interface RunSnapshot { sequence: string; isActive: boolean; modelTurns: number; toolCalls: number }
export interface StudioRun {
  clientId: string; agentId: string; agentName: string; userMessage: string;
  runId?: string; runUrl?: string; eventsUrl?: string;
  startCommand: CommandState; runtimeStatus?: RuntimeStatus; snapshot?: RunSnapshot;
  stateSequenceId: string; eventsBySequence: Record<string, ExecutionEvent>; orderedSequenceIds: string[]; lastSequenceId: string;
  streamState: StreamState; protocolError?: string;
  pendingApproval?: ApprovalDto; approvalCommand: CommandState; cancelCommand: CommandState;
  queryState: 'idle' | 'loading' | 'ready' | 'error'; queryError?: string; queryGeneration: number;
  finalText?: string; failure?: string; isExpanded: boolean; completionAutoCollapseApplied: boolean;
  released?: boolean;
}
export type StudioState = StudioRun[];
export const controlsRun = (run: StudioRun) => !run.released && !terminal(run.runtimeStatus)
  && run.startCommand.state !== 'rejected';
export const canCancel = (run: StudioRun) => run.runtimeStatus === 'Running' && run.snapshot?.isActive === true
  && BigInt(run.snapshot.sequence) >= BigInt(run.stateSequenceId)
  && !['submitting', 'accepted', 'unknown'].includes(run.cancelCommand.state);
