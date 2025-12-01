import { useState, useRef, useEffect } from 'react';
import { MessageCircle, Send, Sparkles, Clock, ChevronDown, ChevronUp, X, Brain, Layers } from 'lucide-react';
import { useRag } from '../../hooks/useRag';
import type { RetrievedMemory } from '../../types/rag';

interface AskMyMemoryProps {
  isExpanded?: boolean;
  onToggle?: () => void;
}

export function AskMyMemory({ isExpanded = true, onToggle }: AskMyMemoryProps) {
  const [query, setQuery] = useState('');
  const [showSources, setShowSources] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  const {
    currentResponse,
    isAsking,
    error,
    ask,
    clearResponse,
    history,
    isLoadingHistory,
  } = useRag({
    maxMemories: 5,
    includeL1: true,
    includeL3: true,
    includeL4: true,
    includeReasoningTrace: false,
  });

  // Auto-resize textarea
  useEffect(() => {
    if (inputRef.current) {
      inputRef.current.style.height = 'auto';
      inputRef.current.style.height = `${Math.min(inputRef.current.scrollHeight, 120)}px`;
    }
  }, [query]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (query.trim() && !isAsking) {
      ask(query.trim());
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSubmit(e);
    }
  };

  const handleHistoryClick = (historyQuery: string) => {
    setQuery(historyQuery);
    setShowHistory(false);
  };

  return (
    <div className="ask-memory-panel">
      {/* Header */}
      <div
        className="flex items-center justify-between cursor-pointer"
        onClick={onToggle}
      >
        <div className="flex items-center gap-2">
          <div className="w-8 h-8 rounded-lg bg-gradient-to-br from-purple-500/20 to-cyan-500/20 flex items-center justify-center">
            <Brain className="w-4 h-4 text-cyan-400" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-soft-white">Ask My Memory</h3>
            <p className="text-xs text-graphite">Query your knowledge</p>
          </div>
        </div>
        {onToggle && (
          isExpanded ? <ChevronUp className="w-4 h-4 text-graphite" /> : <ChevronDown className="w-4 h-4 text-graphite" />
        )}
      </div>

      {isExpanded && (
        <div className="mt-4 space-y-4">
          {/* Input Form */}
          <form onSubmit={handleSubmit} className="space-y-3">
            <div className="relative">
              <textarea
                ref={inputRef}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Ask anything about your memories..."
                className="ask-memory-input"
                rows={1}
                disabled={isAsking}
              />
              <button
                type="button"
                onClick={() => setShowHistory(!showHistory)}
                className="absolute right-12 top-2 p-1.5 text-graphite hover:text-soft-white transition-colors"
                title="Recent queries"
              >
                <Clock className="w-4 h-4" />
              </button>
            </div>

            <button
              type="submit"
              disabled={!query.trim() || isAsking}
              className="btn-primary w-full flex items-center justify-center gap-2 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {isAsking ? (
                <>
                  <div className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                  Thinking...
                </>
              ) : (
                <>
                  <Sparkles className="w-4 h-4" />
                  Ask My Memory
                </>
              )}
            </button>
          </form>

          {/* History Dropdown */}
          {showHistory && history.length > 0 && (
            <div className="history-dropdown animate-in">
              <div className="text-xs text-graphite uppercase tracking-wide mb-2">Recent Questions</div>
              {history.slice(0, 5).map((item) => (
                <button
                  key={item.id}
                  onClick={() => handleHistoryClick(item.query)}
                  className="history-item"
                >
                  <MessageCircle className="w-3 h-3 text-graphite flex-shrink-0 mt-0.5" />
                  <span className="line-clamp-2">{item.query}</span>
                </button>
              ))}
            </div>
          )}

          {/* Error Display */}
          {error && (
            <div className="error-message animate-in">
              <span className="text-red-400 text-sm">{error}</span>
            </div>
          )}

          {/* Response Display */}
          {currentResponse && (
            <div className="response-container animate-in">
              <div className="flex items-start justify-between mb-3">
                <div className="flex items-center gap-2">
                  <Sparkles className="w-4 h-4 text-cyan-400" />
                  <span className="text-xs text-graphite">
                    {currentResponse.latencyMs}ms | {currentResponse.memories.length} sources
                  </span>
                </div>
                <button
                  onClick={clearResponse}
                  className="p-1 text-graphite hover:text-soft-white transition-colors"
                  title="Clear response"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>

              {/* Answer */}
              <div className="response-answer">
                {currentResponse.answer}
              </div>

              {/* Sources Toggle */}
              {currentResponse.memories.length > 0 && (
                <div className="mt-3">
                  <button
                    onClick={() => setShowSources(!showSources)}
                    className="sources-toggle"
                  >
                    <Layers className="w-3 h-3" />
                    {showSources ? 'Hide' : 'Show'} {currentResponse.memories.length} source{currentResponse.memories.length !== 1 ? 's' : ''}
                    {showSources ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
                  </button>

                  {showSources && (
                    <div className="sources-list mt-2 space-y-2">
                      {currentResponse.memories.map((memory) => (
                        <SourceCard key={memory.memoryId} memory={memory} />
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function SourceCard({ memory }: { memory: RetrievedMemory }) {
  const [expanded, setExpanded] = useState(false);

  return (
    <div className="source-card">
      <div
        className="flex items-start justify-between cursor-pointer"
        onClick={() => setExpanded(!expanded)}
      >
        <div className="flex-1 min-w-0">
          <div className="text-sm text-soft-white line-clamp-2">
            {memory.l2OneLiner || memory.l2Summary}
          </div>
          <div className="flex items-center gap-2 mt-1">
            <span className="text-xs text-cyan-400">
              {Math.round(memory.similarity * 100)}% match
            </span>
            {memory.keywords.length > 0 && (
              <span className="text-xs text-graphite">
                {memory.keywords.slice(0, 3).join(', ')}
              </span>
            )}
          </div>
        </div>
        {(memory.l3Facts.length > 0 || memory.l4Heuristics.length > 0) && (
          expanded ? <ChevronUp className="w-4 h-4 text-graphite flex-shrink-0" /> : <ChevronDown className="w-4 h-4 text-graphite flex-shrink-0" />
        )}
      </div>

      {expanded && (
        <div className="mt-2 pt-2 border-t border-slate/30 space-y-2">
          {memory.l1Context && (
            <div>
              <span className="text-xs text-graphite uppercase tracking-wide">Context</span>
              <p className="text-xs text-soft-white/80 mt-1">{memory.l1Context}</p>
            </div>
          )}
          {memory.l3Facts.length > 0 && (
            <div>
              <span className="text-xs text-graphite uppercase tracking-wide">Facts</span>
              <ul className="mt-1 space-y-1">
                {memory.l3Facts.map((fact, i) => (
                  <li key={i} className="text-xs text-soft-white/80 flex items-start gap-1">
                    <span className="text-cyan-400">-</span>
                    {fact}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {memory.l4Heuristics.length > 0 && (
            <div>
              <span className="text-xs text-graphite uppercase tracking-wide">Insights</span>
              <ul className="mt-1 space-y-1">
                {memory.l4Heuristics.map((heuristic, i) => (
                  <li key={i} className="text-xs text-soft-white/80 flex items-start gap-1">
                    <span className="text-purple-400">-</span>
                    {heuristic}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
