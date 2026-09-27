import { MessageSquare, ListOrdered, GitBranch, Wrench } from 'lucide-react';
import type { StudioView } from '../features/inspection/inspectionTypes';
const items = [{ view: 'chat', label: 'Chat', Icon: MessageSquare }, { view: 'trace', label: 'Trace', Icon: ListOrdered }, { view: 'graph', label: 'Graph', Icon: GitBranch }, { view: 'tools', label: 'Tools', Icon: Wrench }] as const;
export function StudioNavigation({ view, onChange, compact = false }: { view: StudioView; onChange: (view: StudioView) => void; compact?: boolean }) {
  return <nav className={compact ? 'studio-navigation compact-navigation' : 'studio-navigation'} aria-label={compact ? 'Mobile Studio views' : 'Studio views'}>
    {items.map(({ view: target, label, Icon }) => <button key={target} aria-current={view === target ? 'page' : undefined} onClick={() => onChange(target)}><Icon size={16} aria-hidden="true" />{label}</button>)}
  </nav>;
}
