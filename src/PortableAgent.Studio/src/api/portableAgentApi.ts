import type { AcceptedDto, AgentDto, RunDto, RunToolsDto } from './contracts';

export class ApiError extends Error {
  constructor(message: string, public kind: 'http' | 'network' | 'protocol', public status?: number, public code?: string) { super(message); }
}
export const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request could not be completed.';

async function request<T>(url: string, init?: RequestInit, expectedStatus?: number): Promise<T> {
  let response: Response;
  try { response = await fetch(url, init); }
  catch (error) {
    if (error instanceof Error && error.name === 'AbortError') throw error;
    throw new ApiError('Cannot reach PortableAgent API.', 'network');
  }
  if (!response.ok) {
    let problem: { detail?: string; code?: string } = {};
    try { problem = await response.json(); } catch { /* Proxy errors need not be ProblemDetails. */ }
    throw new ApiError(problem.detail ?? `API request failed (${response.status}).`, 'http', response.status, problem.code);
  }
  if (expectedStatus && response.status !== expectedStatus) throw new ApiError('The command acknowledgment was invalid.', 'protocol');
  try { return await response.json() as T; }
  catch { throw new ApiError('The API returned an invalid response.', 'protocol'); }
}
async function command(url: string, body?: unknown) {
  const result = await request<AcceptedDto>(url, { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) }, 202);
  if (!result || result.status !== 'accepted' || !result.runId || result.runUrl !== `/api/runs/${result.runId}`
    || result.eventsUrl !== `${result.runUrl}/events`) throw new ApiError('The command acknowledgment was invalid.', 'protocol');
  return result;
}
export const portableAgentApi = {
  getRunTools: (id: string, signal?: AbortSignal) => request<RunToolsDto>(`/api/runs/${id}/tools`, { signal }),
  listAgents: (signal?: AbortSignal) => request<AgentDto[]>('/api/agents', { signal }),
  startRun: (agentId: string, message: string) => command('/api/runs', { agentId, message }),
  getRun: (id: string, signal?: AbortSignal) => request<RunDto>(`/api/runs/${id}`, { signal }),
  submitApproval: (id: string, approval: string, decision: 'approve' | 'reject') => command(`/api/runs/${id}/approvals/${approval}`, { decision }),
  cancelRun: (id: string) => command(`/api/runs/${id}/cancel`),
};
export type PortableAgentApi = typeof portableAgentApi;
