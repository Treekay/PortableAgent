import type { AgentDto } from '../../api/contracts';
export function AgentSelector({ agents, value, onChange, disabled }: { agents: AgentDto[]; value: string; onChange: (id: string) => void; disabled: boolean }) {
  return <label className="agent-selector"><span>Agent</span><select aria-label="Agent" value={value} onChange={e => onChange(e.target.value)} disabled={disabled}>
    {!agents.length && <option value="">No agents available</option>}
    {agents.map(agent => <option key={agent.id} value={agent.id}>{agent.name}</option>)}
  </select></label>;
}
