import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowDown } from 'lucide-react';
import type { StudioState } from './runTypes';
import type { RunController } from './runController';
import { RunBlock } from './RunBlock';
export function RunConversation({ runs, controller, empty }: { runs: StudioState; controller: RunController; empty: ReactNode }) {
  const scroll = useRef<HTMLDivElement>(null), nearBottom = useRef(true);
  const [unseen, setUnseen] = useState(false);
  const contentKey = runs.map(r => `${r.lastSequenceId}:${r.runtimeStatus}:${r.finalText}:${r.isExpanded}:${r.pendingApproval?.approvalId}`).join('|');
  useLayoutEffect(() => {
    const area = scroll.current;
    if (!area) return;
    if (nearBottom.current) { area.scrollTop = area.scrollHeight; setUnseen(false); } else setUnseen(true);
  }, [contentKey]);
  return <div className="conversation-container"><div className="conversation-scroll" ref={scroll} onScroll={() => {
    const area = scroll.current!; nearBottom.current = area.scrollHeight - area.clientHeight - area.scrollTop < 100;
    if (nearBottom.current) setUnseen(false);
  }}><div className="conversation">{runs.length ? runs.map((run, index) => <RunBlock key={run.clientId} run={run} index={index} controller={controller} />) : empty}</div></div>
    {unseen && <button className="new-progress" onClick={() => { const area = scroll.current!; area.scrollTop = area.scrollHeight; nearBottom.current = true; setUnseen(false); }}>New progress <ArrowDown size={14} /></button>}
  </div>;
}
