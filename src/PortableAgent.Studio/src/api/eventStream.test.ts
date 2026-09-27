import { describe, it, expect, vi } from 'vitest';
import { EventStream, parseEvent, validSequence } from './eventStream';
import { eventNames } from './contracts';
import { event, runId, sourceFactory } from '../test/fixtures';
describe('EventStream', () => {
  it('registers every named event and uses canonical lastEventId above Number precision', () => {
    const { factory, sources } = sourceFactory(); const stream = new EventStream(factory), receive = vi.fn();
    stream.open('/events', runId, '0', { event: receive, state: vi.fn() });
    expect([...sources[0].listeners.keys()]).toEqual([...eventNames, 'message']);
    sources[0].emit(event('9007199254740993', 'ModelTurnStarted'));
    expect(receive.mock.calls[0][0].id).toBe('9007199254740993'); stream.close();
  });
  it('reconstruction closes the old source and fences its late callbacks', () => {
    const { factory, sources } = sourceFactory(), receive = vi.fn(), state = vi.fn(), stream = new EventStream(factory);
    stream.open('/events', runId, '0', { event: receive, state });
    stream.open('/events', runId, '12', { event: receive, state });
    expect(sources[0].closed).toBe(true); expect(sources[1].url).toBe('/events?afterSequence=12');
    state.mockClear(); sources[0].emit(event('13', 'RunFailed')); sources[0].error(); expect(receive).not.toHaveBeenCalled(); expect(state).not.toHaveBeenCalled();
    sources[1].emit(event('13', 'RunCompleted')); expect(receive).toHaveBeenCalledOnce(); stream.close();
  });
  it('closes malformed streams without advancing or synthesizing failure', () => {
    const { factory, sources } = sourceFactory(), receive = vi.fn(), state = vi.fn(), stream = new EventStream(factory);
    stream.open('/events', runId, '9', { event: receive, state });
    sources[0].raw('RunCompleted', '10', '{broken');
    expect(sources[0].closed).toBe(true); expect(state).toHaveBeenLastCalledWith('protocol', expect.any(String)); expect(receive).not.toHaveBeenCalled();
  });
  it('distinguishes reconnecting and permanently closed without custom reconnect timers', () => {
    const { factory, sources } = sourceFactory(), state = vi.fn(), stream = new EventStream(factory);
    stream.open('/events', runId, '0', { event: vi.fn(), state });
    sources[0].error(); expect(state).toHaveBeenLastCalledWith('reconnecting');
    sources[0].error(true); expect(state).toHaveBeenLastCalledWith('unavailable'); expect(sources).toHaveLength(1); stream.close();
  });
  it.each(['-1', '1.5', '9223372036854775808', '', '01'])('rejects invalid cursor %s', id => expect(validSequence(id)).toBe(false));
  it('checks envelope identity and retains unknown unnamed envelopes without semantic interpretation', () => {
    const value = event('1', 'FutureProgress');
    expect(parseEvent(new MessageEvent('message', { lastEventId: '1', data: JSON.stringify(value) }), runId, 'message').eventType).toBe('FutureProgress');
    expect(() => parseEvent(new MessageEvent('message', { lastEventId: '2', data: JSON.stringify(value) }), runId, 'message')).toThrow();
  });
});
