import { useEffect, useState } from 'react';
import type { AgentDto } from '../../api/contracts';
import { portableAgentApi } from '../../api/portableAgentApi';
export function useAgents() {
  const [agents, setAgents] = useState<AgentDto[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    setState('loading');
    portableAgentApi.listAgents(abort.signal).then(value => {
      if (!abort.signal.aborted) { setAgents(value); setState('ready'); }
    }).catch(() => { if (!abort.signal.aborted) setState('error'); });
    return () => abort.abort();
  }, [attempt]);
  return { agents, state, retry: () => setAttempt(n => n + 1) };
}
