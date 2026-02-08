import React, { createContext, useContext } from 'react';

interface VsCodeApi {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
}

const VsCodeContext = createContext<VsCodeApi | null>(null);

interface VsCodeProviderProps {
  readonly vscode: VsCodeApi;
  readonly children: React.ReactNode;
}

export function VsCodeProvider({ vscode, children }: VsCodeProviderProps): React.JSX.Element {
  return (
    <VsCodeContext.Provider value={vscode}>
      {children}
    </VsCodeContext.Provider>
  );
}

export function useVsCodeApi(): VsCodeApi {
  const ctx = useContext(VsCodeContext);
  if (!ctx) {
    throw new Error('useVsCodeApi must be used within a VsCodeProvider');
  }
  return ctx;
}
