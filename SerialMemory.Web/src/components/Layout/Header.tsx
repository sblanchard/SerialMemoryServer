import { Brain, Github, ExternalLink } from 'lucide-react';

export function Header() {
  return (
    <header className="bg-navy/80 backdrop-blur-md border-b border-slate/30 px-6 py-3 flex items-center justify-between">
      <div className="flex items-center gap-3">
        <div className="w-9 h-9 rounded-lg bg-gradient-to-br from-electric to-cyan flex items-center justify-center shadow-lg shadow-electric/20">
          <Brain className="w-5 h-5 text-white" />
        </div>
        <div>
          <h1 className="text-lg font-bold gradient-text">
            SerialMemory
          </h1>
          <p className="text-[10px] text-graphite -mt-0.5">Knowledge Graph Explorer</p>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <a
          href="http://localhost:5002"
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center gap-2 text-graphite hover:text-soft-white transition-colors text-sm px-3 py-1.5 rounded-lg hover:bg-slate/20"
        >
          <ExternalLink className="w-4 h-4" />
          <span className="hidden sm:inline">Dashboard</span>
        </a>
        <a
          href="https://github.com/sblanchard/SerialMemoryServer"
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center gap-2 text-graphite hover:text-soft-white transition-colors text-sm px-3 py-1.5 rounded-lg hover:bg-slate/20"
        >
          <Github className="w-4 h-4" />
          <span className="hidden sm:inline">GitHub</span>
        </a>
      </div>
    </header>
  );
}
