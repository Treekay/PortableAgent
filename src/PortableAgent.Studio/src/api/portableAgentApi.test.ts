import { afterEach, describe, expect, it, vi } from 'vitest';
import { portableAgentApi } from './portableAgentApi';
import { ack } from '../test/fixtures';
afterEach(() => vi.unstubAllGlobals());
describe('HTTP API', () => {
  it('sends only the current agent and task with relative URLs', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(ack), { status: 202 })); vi.stubGlobal('fetch', fetch);
    expect(await portableAgentApi.startRun('flight', 'One task')).toEqual(ack);
    expect(fetch).toHaveBeenCalledWith('/api/runs', expect.objectContaining({ method: 'POST', body: JSON.stringify({ agentId: 'flight', message: 'One task' }) }));
  });
  it('normalizes ProblemDetails without turning HTTP errors into Runtime failure', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'This approval was already resolved.', code: 'approval_conflict' }), { status: 409 })));
    await expect(portableAgentApi.submitApproval('run', 'approval', 'reject')).rejects.toMatchObject({ message: 'This approval was already resolved.', kind: 'http', status: 409, code: 'approval_conflict' });
  });
  it('rejects invalid acknowledgments and non-JSON proxy responses', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ ...ack, eventsUrl: 'https://wrong.example/events' }), { status: 202 }))
      .mockResolvedValueOnce(new Response('<html>Proxy error</html>', { status: 502 })); vi.stubGlobal('fetch', fetch);
    await expect(portableAgentApi.startRun('flight', 'task')).rejects.toMatchObject({ kind: 'protocol' });
    await expect(portableAgentApi.listAgents()).rejects.toMatchObject({ kind: 'http', status: 502 });
  });
});
