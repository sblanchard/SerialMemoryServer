import type { ReactNode } from 'react';
import { Header } from './Header';

interface MainLayoutProps {
  sidebar: ReactNode;
  children: ReactNode;
}

export function MainLayout({ sidebar, children }: MainLayoutProps) {
  return (
    <div className="h-screen w-screen flex flex-col overflow-hidden bg-bg-primary">
      <Header />

      <div className="flex-1 flex overflow-hidden">
        {/* Sidebar */}
        <aside className="w-80 bg-bg-secondary border-r border-gray-700 flex flex-col overflow-hidden">
          <div className="flex-1 overflow-y-auto p-4 space-y-6">
            {sidebar}
          </div>
        </aside>

        {/* Main content */}
        <main className="flex-1 relative overflow-hidden">
          {children}
        </main>
      </div>
    </div>
  );
}
