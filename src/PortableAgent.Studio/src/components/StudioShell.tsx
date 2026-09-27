import type { ReactNode } from 'react';
import { Box } from 'lucide-react';
import { StudioNavigation } from './StudioNavigation';
import type { StudioView } from '../features/inspection/inspectionTypes';
export function StudioShell({ header, children, composer, view, onView }: { header: ReactNode; children: ReactNode; composer: ReactNode; view: StudioView; onView: (view: StudioView) => void }) {
  return <div className="studio-shell">
    <aside className="rail"><div className="brand"><Box size={20} aria-hidden="true" /><span>PortableAgent</span></div>
      <StudioNavigation view={view} onChange={onView} />
      <div className="rail-footer">DEVELOPER STUDIO<br /><span>Local workspace</span></div>
    </aside>
    <main className="workspace"><header className="topbar"><div><p className="eyebrow">WORKSPACE</p><h1>PortableAgent Studio</h1></div><div className="header-controls">{header}</div></header>
      <StudioNavigation view={view} onChange={onView} compact />
      <div className="workspace-body">{children}</div><footer className="composer-dock" hidden={view !== 'chat'}>{composer}</footer>
    </main>
  </div>;
}
