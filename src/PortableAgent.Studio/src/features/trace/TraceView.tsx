import { useLayoutEffect, useRef, useState } from 'react';
import type { RunInspection } from '../inspection/inspectionTypes';
import { traceProjection, type TraceFilter } from './traceProjection';
import { TraceToolbar } from './TraceToolbar';
import { TraceEventRow } from './TraceEventRow';
export function TraceView({ inspection, active }: { inspection: RunInspection; active: boolean }) {
  const [filter, setFilter] = useState<TraceFilter>('All'), [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const [unseen, setUnseen] = useState(false), scroll = useRef<HTMLDivElement>(null), near = useRef(true), position = useRef(0), wasActive = useRef(active);
  const rows = traceProjection(inspection, filter), signature = inspection.orderedEvents.map(e => e.event.id).join(',');
  useLayoutEffect(() => {
    const area = scroll.current;
    if (active && area) {
      if (!wasActive.current) area.scrollTop = position.current;
      else if (near.current) area.scrollTop = area.scrollHeight;
      else setUnseen(true);
    } else if (!active) setUnseen(true);
    wasActive.current = active;
  }, [signature, active]);
  return <div className="trace-view"><TraceToolbar filter={filter} onChange={setFilter} />
    <div className="trace-scroll" ref={scroll} onScroll={() => { if (active && scroll.current) { const area = scroll.current; position.current = area.scrollTop; near.current = area.scrollHeight - area.clientHeight - area.scrollTop < 80; if (near.current) setUnseen(false); } }}>
      <ol className="trace-list">{rows.map(({ observation, summary }) => <TraceEventRow key={observation.event.id} observation={observation} summary={summary} expanded={expanded.has(observation.event.id)} toggle={() => setExpanded(previous => { const next = new Set(previous); if (next.has(observation.event.id)) next.delete(observation.event.id); else next.add(observation.event.id); return next; })} />)}</ol>
      {!rows.length && <p className="muted">No events in this category have been observed.</p>}
    </div>{unseen && <button className="new-progress" onClick={() => { if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight; near.current = true; setUnseen(false); }}>New events ↓</button>}
  </div>;
}
