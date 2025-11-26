import { Brain, Github } from 'lucide-react';

export function Header() {
  return (
    <header className="bg-bg-secondary border-b border-gray-700 px-6 py-4 flex items-center justify-between">
      <div className="flex items-center gap-3">
        <div className="w-10 h-10 rounded-lg bg-gradient-to-br from-primary to-pink-500 flex items-center justify-center">
          <Brain className="w-6 h-6 text-white" />
        </div>
        <div>
          <h1 className="text-xl font-bold bg-gradient-to-r from-primary to-pink-400 bg-clip-text text-transparent">
            SerialMemory
          </h1>
          <p className="text-xs text-gray-500">Knowledge Graph Explorer</p>
        </div>
      </div>

      <div className="flex items-center gap-4">
        <a
          href="https://github.com/sblanchard/SerialMemoryServer"
          target="_blank"
          rel="noopener noreferrer"
          className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors"
        >
          <Github className="w-5 h-5" />
          <span className="text-sm hidden sm:inline">GitHub</span>
        </a>
      </div>
    </header>
  );
}
