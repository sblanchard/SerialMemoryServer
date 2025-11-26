import { useState, useCallback, useRef, useEffect, type ReactNode } from 'react';
import { Header } from './Header';

interface MainLayoutProps {
  sidebar: ReactNode;
  children: ReactNode;
}

export function MainLayout({ sidebar, children }: MainLayoutProps) {
  const [sidebarWidth, setSidebarWidth] = useState(400);
  const [isDragging, setIsDragging] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const MIN_WIDTH = 280;
  const MAX_WIDTH = 600;

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    setIsDragging(true);
  }, []);

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (!isDragging || !containerRef.current) return;

    const containerRect = containerRef.current.getBoundingClientRect();
    const newWidth = e.clientX - containerRect.left;

    if (newWidth >= MIN_WIDTH && newWidth <= MAX_WIDTH) {
      setSidebarWidth(newWidth);
    }
  }, [isDragging]);

  const handleMouseUp = useCallback(() => {
    setIsDragging(false);
  }, []);

  useEffect(() => {
    if (isDragging) {
      document.addEventListener('mousemove', handleMouseMove);
      document.addEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
    }

    return () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
  }, [isDragging, handleMouseMove, handleMouseUp]);

  return (
    <div className="h-screen w-screen flex flex-col overflow-hidden bg-midnight">
      <Header />

      <div ref={containerRef} className="flex-1 flex overflow-hidden">
        {/* Sidebar */}
        <aside
          className="bg-navy/50 backdrop-blur-sm flex flex-col overflow-hidden border-r border-slate/30"
          style={{ width: sidebarWidth, minWidth: MIN_WIDTH, maxWidth: MAX_WIDTH }}
        >
          <div className="flex-1 overflow-y-auto p-4 space-y-5">
            {sidebar}
          </div>
        </aside>

        {/* Resizable handle */}
        <div
          className={`w-1 hover:w-1.5 bg-slate/30 hover:bg-electric/50 cursor-col-resize transition-all duration-150 flex-shrink-0 ${
            isDragging ? 'w-1.5 bg-electric' : ''
          }`}
          onMouseDown={handleMouseDown}
        >
          <div className="h-full w-full flex items-center justify-center">
            <div className={`w-0.5 h-12 rounded-full transition-colors ${
              isDragging ? 'bg-electric' : 'bg-slate/50'
            }`} />
          </div>
        </div>

        {/* Main content - graph area */}
        <main className="flex-1 relative overflow-hidden bg-midnight flex items-center justify-center">
          {children}
        </main>
      </div>
    </div>
  );
}
