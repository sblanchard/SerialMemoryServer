import React from 'react';
import { createRoot } from 'react-dom/client';
import { CanvasApp } from './CanvasApp';
import { VsCodeProvider } from './hooks/VsCodeContext';

declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

const vscode = acquireVsCodeApi();

const container = document.getElementById('root');
if (container) {
  const root = createRoot(container);
  root.render(
    <VsCodeProvider vscode={vscode}>
      <CanvasApp vscode={vscode} />
    </VsCodeProvider>,
  );
}
