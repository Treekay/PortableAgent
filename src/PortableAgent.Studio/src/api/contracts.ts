export interface AgentDto { id: string; name: string }
export interface ToolCatalogEntryDto {
  toolId: { sourceId: string; name: string }; modelName: string; description: string; inputSchema: unknown;
}
export interface RunToolsDto { runId: string; status: RuntimeStatus; snapshotSequence: number; tools: ToolCatalogEntryDto[] }
export type RuntimeStatus = 'Running' | 'AwaitingApproval' | 'Completed' | 'Failed' | 'Cancelled' | 'LimitReached';
export const terminal = (status?: RuntimeStatus) => !!status && ['Completed', 'Failed', 'Cancelled', 'LimitReached'].includes(status);
export interface AcceptedDto { runId: string; status: 'accepted'; runUrl: string; eventsUrl: string; approvalId?: string }
export interface ApprovalDto {
  approvalId: string; toolName: string; toolId: { sourceId: string; name: string };
  arguments: unknown; policyReason: string | null; createdAt: string;
}
export interface RunDto {
  runId: string; agentId: string | null; status: RuntimeStatus; modelTurns: number; toolCalls: number;
  snapshotSequence: number; isActive: boolean; finalText: string | null;
  failure: { code: string; message: string } | null; pendingApproval: ApprovalDto | null;
}
export interface ExecutionEvent {
  id: string; // Exact SSE lastEventId, never a JS number.
  eventId: string; runId: string; sequence: number; occurredAt: string;
  eventType: string; payload: Record<string, unknown>;
}
export const eventNames = [
  'RunStarted', 'ToolDiscoveryStarted', 'ToolDiscoveryCompleted', 'ModelTurnStarted', 'ModelTurnCompleted',
  'ToolCallProposed', 'PolicyEvaluationStarted', 'PolicyEvaluationCompleted', 'ApprovalRequired', 'ApprovalResolved',
  'RunResumed', 'ToolExecutionStarted', 'ToolExecutionCompleted', 'RunCompleted', 'RunFailed', 'RunCancelled', 'RunLimitReached',
] as const;
