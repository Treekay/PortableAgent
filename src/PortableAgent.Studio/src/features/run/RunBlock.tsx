import type { StudioRun } from './runTypes';
import type { RunController } from './runController';
import { UserMessage } from './UserMessage';
import { ExecutionCard } from './ExecutionCard';
import { AssistantMessage } from './AssistantMessage';
export function RunBlock({ run, controller, index }: { run: StudioRun; controller: RunController; index: number }) {
  return <article className="run-block" aria-label={`Run ${index + 1}`}><div className="run-meta">TASK {String(index + 1).padStart(2, '0')}<span>{run.agentName}</span></div>
    <UserMessage text={run.userMessage} /><ExecutionCard run={run} controller={controller} />
    {run.runtimeStatus === 'Completed' && run.finalText !== undefined && <AssistantMessage text={run.finalText} />}
  </article>;
}
