import { useEffect, useRef, useState } from 'react';
import { HubConnectionBuilder, HubConnection, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { SearchMemory } from '../types/graph';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

interface MemoryEventPayload {
  eventId: string;
  tenantId: string;
  memoryId: string;
  eventType: string;
  actor: string;
  payload: {
    content: string;
    content_preview: string;
    source: string;
    layer: string;
    entities?: Array<{ name: string; type: string }>;
  };
  timestamp: string;
}

interface UseSignalROptions {
  enabled?: boolean;
  onMemoryEvent?: (memory: SearchMemory) => void;
}

export function useSignalR({ enabled = true, onMemoryEvent }: UseSignalROptions = {}) {
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');
  const connectionRef = useRef<HubConnection | null>(null);
  const onMemoryEventRef = useRef(onMemoryEvent);
  onMemoryEventRef.current = onMemoryEvent;

  useEffect(() => {
    if (!enabled) return;

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    connection.onreconnecting(() => setStatus('reconnecting'));
    connection.onreconnected(() => {
      setStatus('connected');
      subscribeToStreams(connection);
    });
    connection.onclose(() => setStatus('disconnected'));

    connection.on('MemoryEvent', (evt: MemoryEventPayload) => {
      if (evt.eventType !== 'created') return;

      const entities = (evt.payload.entities || []).map((e, i) => ({
        id: `${evt.memoryId}-entity-${i}`,
        name: e.name,
        type: e.type,
      }));

      const memory: SearchMemory = {
        id: evt.memoryId,
        content: evt.payload.content || evt.payload.content_preview || '',
        createdAt: evt.timestamp,
        entities,
      };

      onMemoryEventRef.current?.(memory);
    });

    setStatus('connecting');
    connection.start()
      .then(() => {
        setStatus('connected');
        subscribeToStreams(connection);
      })
      .catch(() => setStatus('disconnected'));

    return () => {
      connection.stop();
      connectionRef.current = null;
    };
  }, [enabled]);

  const isConnected = status === 'connected';

  return { status, isConnected };
}

async function subscribeToStreams(connection: HubConnection) {
  if (connection.state !== HubConnectionState.Connected) return;
  try {
    await connection.invoke('SubscribeMemoryEvents');
  } catch {
    // Self-hosted mode may not have tenant context, fall back to events.all
    try {
      await connection.invoke('SubscribeToStream', 'events.all');
    } catch {
      // Subscription failed silently
    }
  }
}
