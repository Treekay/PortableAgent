import { Check, Circle, LoaderCircle, TriangleAlert, Minus } from 'lucide-react';
import type { ExecutionGroup } from './eventPresentation';
export function ExecutionStep({ group }: { group: ExecutionGroup }) {
  const Icon = group.state === 'done' ? Check : group.state === 'active' ? LoaderCircle : group.state === 'error' ? TriangleAlert : group.state === 'muted' ? Minus : Circle;
  return <li className={`execution-step ${group.state}`}><Icon size={15} aria-hidden="true" className={group.state === 'active' ? 'spinner' : ''} />
    <div><span className="step-title">{group.title}</span><span className="step-detail">{group.detail}</span></div></li>;
}
