import { it, expect } from 'vitest';
import { eventPresentation } from './eventPresentation';
import { event } from '../../test/fixtures';
it('groups lifecycle pairs and preserves approval-resume policy history', () => {
  const raw = [event('1', 'ToolDiscoveryStarted'), event('2', 'ToolDiscoveryCompleted', { toolCount: 2 }), event('3', 'ModelTurnStarted', { turn: 1 }), event('4', 'ModelTurnCompleted', { turn: 1 }),
    event('5', 'ToolCallProposed', { callId: 'c', toolName: 'cancel_booking' }), event('6', 'PolicyEvaluationCompleted', { callId: 'c', outcome: 'RequireApproval' }),
    event('7', 'ApprovalRequired', { callId: 'c' }), event('8', 'ApprovalResolved', { callId: 'c', status: 'Approved' }), event('9', 'RunResumed'), event('10', 'PolicyEvaluationCompleted', { callId: 'c', outcome: 'Allow' })];
  const before = JSON.stringify(raw); const groups = eventPresentation(raw);
  expect(groups.filter(g => g.title === 'Tools')).toHaveLength(1);
  expect(groups.filter(g => g.title === 'Policy check').map(g => g.detail)).toEqual(['Requires approval', 'Allow']);
  expect(JSON.stringify(raw)).toBe(before);
});
it('shows business failure while preserving later model completion and denial evidence', () => {
  const groups = eventPresentation([event('1', 'ToolExecutionCompleted', { callId: 'c', toolName: 'lookup', success: false }),
    event('2', 'ModelTurnCompleted', { turn: 2 }), event('3', 'PolicyEvaluationCompleted', { callId: 'd', outcome: 'Deny' })]);
  expect(groups.find(g => g.title === 'lookup')).toMatchObject({ state: 'error', detail: 'Returned failure' });
  expect(groups.find(g => g.title === 'Model turn 2')?.state).toBe('done');
  expect(groups.some(g => g.detail === 'Blocked by policy')).toBe(true);
});
