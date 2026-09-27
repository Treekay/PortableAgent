import { traceFilters, type TraceFilter } from './traceProjection';
export function TraceToolbar({ filter, onChange }: { filter: TraceFilter; onChange: (filter: TraceFilter) => void }) {
  return <div className="trace-filters" role="group" aria-label="Event category">{traceFilters.map(f => <button key={f} aria-pressed={filter === f} onClick={() => onChange(f)}>{f}</button>)}</div>;
}
