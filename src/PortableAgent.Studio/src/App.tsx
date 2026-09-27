import { useState } from 'react';
import { ArrowUpRight, Workflow } from 'lucide-react';
import { StudioShell } from './components/StudioShell';
import { ConnectionStatus } from './components/ConnectionStatus';
import { useAgents } from './features/agents/useAgents';
import { AgentSelector } from './features/agents/AgentSelector';
import { useRunExecution } from './features/run/useRunExecution';
import { controlsRun } from './features/run/runTypes';
import { Composer } from './features/run/Composer';
import { RunConversation } from './features/run/RunConversation';
import { demoPrompts } from './features/run/demoPrompts';
import type { RunController } from './features/run/runController';

export default function App({ runController }: { runController?: RunController }) {
  const agents = useAgents();
  const { runs, controller } = useRunExecution(runController);
  const [selection, setSelection] = useState(''), [draft, setDraft] = useState('');
  const agent = agents.agents.find(a => a.id === selection) ?? agents.agents[0];
  const busy = runs.some(controlsRun);
  const send = async () => {
    if (!agent) return;
    const submitted = draft;
    if (await controller.start(agent, submitted)) setDraft(current => current === submitted ? '' : current);
  };
  const important = runs.at(-1)?.runtimeStatus;
  return <StudioShell header={<><AgentSelector agents={agents.agents} value={agent?.id ?? ''} onChange={setSelection} disabled={busy || agents.state !== 'ready'} /><ConnectionStatus state={agents.state} /></>}
    composer={<Composer value={draft} onChange={setDraft} onSubmit={() => { void send(); }} disabled={busy || agents.state !== 'ready' || !agent} busy={busy} />}>
    <div className="sr-only" aria-live="polite">{important === 'AwaitingApproval' ? 'Approval required' : important === 'Completed' ? 'Run completed' : important === 'Failed' ? 'Run failed' : ''}</div>
    <RunConversation runs={runs} controller={controller} empty={agents.state === 'error'
      ? <div className="empty-state"><Workflow size={28} /><h2>Cannot connect to PortableAgent API.</h2><p>Make sure the API is running on localhost:5100.</p><button onClick={agents.retry}>Retry</button></div>
      : <div className="empty-state"><div className="empty-mark"><Workflow size={26} /></div><h2>Choose an agent and run a task.</h2><p>Follow execution progress and review operations that need approval.</p>
        {agents.state === 'loading' ? <p>Loading agents…</p> : <div className="suggestions"><span className="field-label">LOCAL DEMO EXAMPLES</span>{(demoPrompts[agent?.id ?? ''] ?? []).map(prompt => <button key={prompt} onClick={() => setDraft(prompt)}>{prompt}<ArrowUpRight size={16} /></button>)}</div>}
      </div>} />
  </StudioShell>;
}
