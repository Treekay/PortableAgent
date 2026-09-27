import { event, runId } from './fixtures';
import type { RunToolsDto } from '../api/contracts';
export function flightEvents() {
  return [event('1', 'RunStarted'), event('2', 'ToolDiscoveryStarted'), event('3', 'ToolDiscoveryCompleted', { toolCount: 2 }),
    event('4', 'ModelTurnStarted', { turn: 1 }), event('5', 'ModelTurnCompleted', { turn: 1, finishReason: 'ToolCalls', toolCallCount: 1 }),
    event('6', 'ToolCallProposed', { callId: 'call-1', toolName: 'cancel_booking' }), event('7', 'PolicyEvaluationStarted', { callId: 'call-1' }),
    event('8', 'PolicyEvaluationCompleted', { callId: 'call-1', outcome: 'RequireApproval', policyId: 'confirm-flight-cancellation-v1' }),
    event('9', 'ApprovalRequired', { callId: 'call-1', approvalId: 'approval-1' }), event('10', 'ApprovalResolved', { callId: 'call-1', approvalId: 'approval-1', status: 'Approved' }),
    event('11', 'RunResumed'), event('12', 'ToolDiscoveryStarted'), event('13', 'ToolDiscoveryCompleted', { toolCount: 2 }),
    event('14', 'PolicyEvaluationStarted', { callId: 'call-1' }), event('15', 'PolicyEvaluationCompleted', { callId: 'call-1', outcome: 'RequireApproval' }),
    event('16', 'ToolExecutionStarted', { callId: 'call-1', toolName: 'cancel_booking', toolId: 'flight-mcp/cancel_booking' }),
    event('17', 'ToolExecutionCompleted', { callId: 'call-1', toolName: 'cancel_booking', success: true }),
    event('18', 'ModelTurnStarted', { turn: 2 }), event('19', 'ModelTurnCompleted', { turn: 2, finishReason: 'Completed', toolCallCount: 0 }),
    event('20', 'RunCompleted', { status: 'Completed', modelTurns: 2, toolCalls: 1 })];
}
export function catalog(values: Partial<RunToolsDto> = {}): RunToolsDto {
  return { runId, status: 'Completed', snapshotSequence: 20, tools: ['get_booking', 'cancel_booking'].map(modelName => ({ toolId: { sourceId: 'flight-mcp', name: modelName }, modelName,
    description: modelName === 'get_booking' ? 'Read a booking.' : 'Cancel a booking.', inputSchema: { type: 'object', properties: { bookingId: { type: 'string' } }, required: ['bookingId'], additionalProperties: false } })), ...values };
}
