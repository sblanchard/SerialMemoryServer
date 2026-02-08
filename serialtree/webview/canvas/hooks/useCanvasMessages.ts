import { useEffect, useRef } from 'react';
import type { CanvasNode, CanvasEdge, CanvasMessageToWebview } from '../types';

interface UseCanvasMessagesOptions {
  readonly onCanvasData: (nodes: readonly CanvasNode[], edges: readonly CanvasEdge[]) => void;
  readonly onNodeAdded: (node: CanvasNode, edges: readonly CanvasEdge[]) => void;
  readonly onNodeUpdated: (node: CanvasNode) => void;
  readonly onAgentStatusChanged: (agentId: string, status: string) => void;
  readonly onSettings: (layout: string, showMinimap: boolean) => void;
}

export function useCanvasMessages(options: UseCanvasMessagesOptions): void {
  const optionsRef = useRef(options);
  optionsRef.current = options;

  useEffect(() => {
    const handler = (event: MessageEvent) => {
      const message = event.data as CanvasMessageToWebview;
      if (!message || typeof message.type !== 'string') {
        return;
      }

      const opts = optionsRef.current;

      switch (message.type) {
        case 'canvasData':
          opts.onCanvasData(message.nodes, message.edges);
          break;
        case 'nodeAdded':
          opts.onNodeAdded(message.node, message.edges);
          break;
        case 'nodeUpdated':
          opts.onNodeUpdated(message.node);
          break;
        case 'agentStatusChanged':
          opts.onAgentStatusChanged(message.agentId, message.status);
          break;
        case 'settings':
          opts.onSettings(message.layout, message.showMinimap);
          break;
      }
    };

    window.addEventListener('message', handler);
    return () => window.removeEventListener('message', handler);
  }, []);
}
