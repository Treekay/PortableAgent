import { describe, expect, it } from 'vitest';
import { graphProjection } from './graphProjection';
import { graphLayout } from './graphLayout';
import { runInspection } from '../inspection/runInspection';
import { event, runId } from '../../test/fixtures';
import { flightEvents } from '../../test/inspectionFixtures';
const project = (events: ReturnType<typeof flightEvents>, problem = false) => graphProjection(runInspection(events), runId, problem);
describe('actual execution graph', () => {
  it('projects one continuous approval Run with one proposal and two operation stages', () => {
    const graph = project(flightEvents());
    expect(graph.nodes.map(n => n.kind)).toEqual(['Run Start', 'Tool Discovery', 'Model Turn', 'Tool Operation', 'Approval', 'Run Resume', 'Tool Discovery', 'Tool Operation', 'Model Turn', 'Terminal']);
    const ops = graph.nodes.filter(n => n.kind === 'Tool Operation'); expect(ops[0].callId).toBe(ops[1].callId); expect(ops[0].id).not.toBe(ops[1].id);
    expect(ops.flatMap(n => n.events).filter(o => o.event.eventType === 'ToolCallProposed')).toHaveLength(1);
    expect(ops[0].details[0]).toContain('RequireApproval'); expect(ops[1].details[0]).toContain('RequireApproval'); expect(ops[1].status).toBe('Completed');
    // Resume closes the proposal stage even before the next model turn arrives.
    expect(project(flightEvents().slice(0, 11)).nodes.find(n => n.kind === 'Tool Operation')?.status).toBe('Not executed');
    for (const edge of graph.edges) expect(graph.nodes.findIndex(n => n.id === edge.source)).toBeLessThan(graph.nodes.findIndex(n => n.id === edge.target));
    expect(graph.edges.filter(e => e.meaning === 'relation')).toHaveLength(2); expect(graph.edges.some(e => e.meaning === 'order')).toBe(true);
  });
  it('renders simple reads without approval or invented reasoning', () => {
    const graph = project(flightEvents().filter(e => BigInt(e.id) <= 8n || BigInt(e.id) >= 16n));
    expect(graph.nodes.filter(n => n.kind === 'Tool Operation')).toHaveLength(1); expect(graph.nodes.some(n => n.kind === 'Approval')).toBe(false);
    expect(JSON.stringify(graph)).not.toMatch(/Thinking|Reasoning|Thoughts|Skipped due to prior failure/);
  });
  it('retains rejection and no execution', () => {
    const events = flightEvents().filter(e => BigInt(e.id) <= 13n || BigInt(e.id) >= 18n).map(e => e.eventType === 'ApprovalResolved' ? { ...e, payload: { ...e.payload, status: 'Rejected' } } : e);
    const graph = project(events);
    expect(graph.nodes.find(n => n.kind === 'Approval')?.status).toBe('Rejected'); expect(graph.nodes.find(n => n.kind === 'Tool Operation')?.status).toBe('Not executed');
  });
  it('denial is explicit and does not require execution', () => {
    const graph = project([event('1', 'ToolCallProposed', { callId: 'a', toolName: 'write' }), event('2', 'PolicyEvaluationCompleted', { callId: 'a', outcome: 'Deny' }), event('3', 'RunCompleted')]);
    expect(graph.nodes[0].status).toBe('Blocked by policy'); expect(graph.nodes[0].events.some(o => o.event.eventType === 'ToolExecutionCompleted')).toBe(false);
  });
  it('tool business failure can precede a later model turn and successful Run', () => {
    const graph = project(flightEvents().map(e => e.eventType === 'ToolExecutionCompleted' ? { ...e, payload: { ...e.payload, success: false } } : e));
    expect(graph.nodes.find(n => n.anchor === '14')?.status).toBe('Returned failure'); expect(graph.nodes.at(-1)?.status).toBe('Completed');
  });
  it('batch calls are sibling nodes without dependency edges', () => {
    const graph = project([event('1', 'ModelTurnCompleted', { turn: 1 }), ...['a', 'b', 'c'].map((callId, i) => event(String(i + 2), 'ToolCallProposed', { callId, toolName: callId })), event('8', 'ModelTurnStarted', { turn: 2 })]);
    const calls = graph.nodes.filter(n => n.kind === 'Tool Operation'); expect(calls).toHaveLength(3);
    expect(graph.edges.some(e => calls.some(c => c.id === e.source) && calls.some(c => c.id === e.target))).toBe(false);
    const positions = graphLayout(graph); expect(new Set(calls.map(c => positions.get(c.id)?.x)).size).toBe(1); expect(new Set(calls.map(c => positions.get(c.id)?.y)).size).toBe(3);
  });
  it.each([[false, false, 'Execution not observed yet'], [true, false, 'Not executed'], [true, true, 'Execution not observed']] as const)('missing execution stays conservative (%s, %s)', (ended, problem, status) => {
    const events = [event('1', 'ToolCallProposed', { callId: 'a' }), event('2', 'PolicyEvaluationCompleted', { callId: 'a', outcome: 'Allow' })];
    if (ended) events.push(event('3', 'RunCompleted'));
    expect(project(events, problem).nodes[0].status).toBe(status);
  });
  it('a start without completion does not claim success or rollback', () => {
    const graph = project([event('1', 'ToolExecutionStarted', { callId: 'a' }), event('2', 'RunCancelled')]);
    expect(graph.nodes[0].status).toBe('Completion not observed');
  });
  it('duplicate call proposals have separate stable display occurrences', () => {
    const graph = project([event('1', 'ToolCallProposed', { callId: 'a' }), event('2', 'ToolCallProposed', { callId: 'a' }), event('3', 'PolicyEvaluationCompleted', { callId: 'a', outcome: 'Allow' })]);
    expect(new Set(graph.nodes.map(n => n.id)).size).toBe(3); expect(graph.nodes.every(n => n.details.some(d => d.includes('Ambiguous')))).toBe(true);
  });
  it('live updates preserve existing IDs and deterministic layout; no synthetic terminal', () => {
    const before = project(flightEvents().slice(0, 9)), after = project(flightEvents());
    for (const node of before.nodes) expect(after.nodes.some(n => n.id === node.id)).toBe(true);
    expect(before.nodes.some(n => n.kind === 'Terminal')).toBe(false); expect(graphLayout(after)).toEqual(graphLayout(project(flightEvents())));
  });
});
