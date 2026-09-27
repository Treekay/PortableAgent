import type { Observation } from '../inspection/inspectionTypes';
export function TraceEventRow({ observation, summary, expanded, toggle }: { observation: Observation; summary: string; expanded: boolean; toggle: () => void }) {
  const { event } = observation;
  const local = new Date(event.occurredAt).toLocaleTimeString(undefined, { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
  return <li className="trace-row"><button className="trace-row-toggle" aria-label={`#${event.id} ${local} ${event.eventType} · ${summary}`} aria-expanded={expanded} onClick={toggle}>
    <code>#{event.id}</code><time dateTime={event.occurredAt}>{local}</time><span><strong>{event.eventType}</strong><span>{summary}</span>
      {observation.callId && <code className="trace-call">{observation.callId}</code>}</span></button>
    {expanded && <div className="trace-payload"><dl><dt>EventId</dt><dd>{event.eventId}</dd><dt>RunId</dt><dd>{event.runId}</dd><dt>Sequence</dt><dd>{event.id}</dd><dt>OccurredAt</dt><dd>{event.occurredAt}</dd><dt>EventType</dt><dd>{event.eventType}</dd></dl><pre aria-label={`Payload #${event.id}`}>{JSON.stringify(event.payload, null, 2)}</pre></div>}
  </li>;
}
