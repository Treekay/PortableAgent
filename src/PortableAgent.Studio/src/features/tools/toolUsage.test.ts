import { expect, it } from 'vitest';
import { toolUsage } from './toolUsage';
import { runInspection } from '../inspection/runInspection';
import { catalog, flightEvents } from '../../test/inspectionFixtures';
import { event } from '../../test/fixtures';
it('joins trusted identity and retains every policy, approval and execution observation', () => {
  const usage = toolUsage(catalog().tools, runInspection(flightEvents()));
  expect(usage.entries[0].observations).toHaveLength(0);
  const observed = usage.entries[1].observations[0]; expect(observed.association).toBe('Trusted identity'); expect(observed.call.policyEvaluations).toHaveLength(4);
  expect(observed.call.executionStarts).toHaveLength(1); expect(observed.call.executionCompletions).toHaveLength(1);
});
it('marks name-only associations for denied calls and keeps unmatched/ambiguous observations separate', () => {
  const events = [event('1', 'ToolCallProposed', { callId: 'a', toolName: 'cancel_booking' }), event('2', 'PolicyEvaluationCompleted', { callId: 'a', outcome: 'Deny' }),
    event('3', 'ToolCallProposed', { callId: 'b', toolName: 'removed' }), event('4', 'ToolCallProposed', { callId: 'c', toolName: 'get_booking' }), event('5', 'ToolCallProposed', { callId: 'c', toolName: 'get_booking' })];
  const result = toolUsage(catalog().tools, runInspection(events)); expect(result.entries[1].observations[0].association).toBe('ModelName match only'); expect(result.unassociated).toHaveLength(2);
});
it('does not fall back to matching names when explicit historical identity disagrees', () => {
  const events = [event('1', 'ToolExecutionStarted', { callId: 'a', toolName: 'cancel_booking', toolId: 'old-source/cancel_booking' })];
  const result = toolUsage(catalog().tools, runInspection(events)); expect(result.unassociated).toHaveLength(1); expect(result.entries.every(e => !e.observations.length)).toBe(true);
});
