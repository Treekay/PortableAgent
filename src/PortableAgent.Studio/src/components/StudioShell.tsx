import type { ReactNode } from 'react';
import { Box, MessageSquare } from 'lucide-react';
export function StudioShell({ header, children, composer }: { header: ReactNode; children: ReactNode; composer: ReactNode }) {
  return <div className="studio-shell">
    <aside className="rail"><div className="brand"><Box size={20} aria-hidden="true" /><span>PortableAgent</span></div>
      <nav aria-label="Studio"><a href="/" aria-current="page" onClick={event => event.preventDefault()}><MessageSquare size={17} aria-hidden="true" /><span>Chat</span></a></nav>
      <div className="rail-footer">DEVELOPER STUDIO<br /><span>Local workspace</span></div>
    </aside>
    <main className="workspace"><header className="topbar"><div><p className="eyebrow">WORKSPACE</p><h1>PortableAgent Studio</h1></div><div className="header-controls">{header}</div></header>
      {children}<footer className="composer-dock">{composer}</footer>
    </main>
  </div>;
}
