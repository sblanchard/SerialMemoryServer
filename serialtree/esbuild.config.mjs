import * as esbuild from 'esbuild';

const isProduction = process.argv.includes('--production');
const isWatch = process.argv.includes('--watch');

// Extension bundle (Node.js, CommonJS)
const extensionConfig = {
  entryPoints: ['src/extension.ts'],
  bundle: true,
  outfile: 'dist/extension.js',
  external: ['vscode'],
  format: 'cjs',
  platform: 'node',
  target: 'node18',
  sourcemap: !isProduction,
  minify: isProduction,
  treeShaking: true,
};

// Graph webview bundle (browser, IIFE)
const graphWebviewConfig = {
  entryPoints: ['webview/graph/graph.tsx'],
  bundle: true,
  outfile: 'dist/webview/graph.js',
  format: 'iife',
  platform: 'browser',
  target: 'es2020',
  sourcemap: !isProduction,
  minify: isProduction,
  treeShaking: true,
  jsx: 'automatic',
  loader: { '.tsx': 'tsx', '.ts': 'ts' },
  define: {
    'process.env.NODE_ENV': isProduction ? '"production"' : '"development"',
  },
};

// Search webview bundle (browser, IIFE)
const searchWebviewConfig = {
  entryPoints: ['webview/search/search.tsx'],
  bundle: true,
  outfile: 'dist/webview/search.js',
  format: 'iife',
  platform: 'browser',
  target: 'es2020',
  sourcemap: !isProduction,
  minify: isProduction,
  treeShaking: true,
  jsx: 'automatic',
  loader: { '.tsx': 'tsx', '.ts': 'ts' },
  define: {
    'process.env.NODE_ENV': isProduction ? '"production"' : '"development"',
  },
};

// Canvas webview bundle (browser, IIFE)
const canvasWebviewConfig = {
  entryPoints: ['webview/canvas/canvas.tsx'],
  bundle: true,
  outfile: 'dist/webview/canvas.js',
  format: 'iife',
  platform: 'browser',
  target: 'es2020',
  sourcemap: !isProduction,
  minify: isProduction,
  treeShaking: true,
  jsx: 'automatic',
  loader: { '.tsx': 'tsx', '.ts': 'ts', '.css': 'css' },
  define: {
    'process.env.NODE_ENV': isProduction ? '"production"' : '"development"',
  },
};

async function build() {
  const configs = [extensionConfig, graphWebviewConfig, searchWebviewConfig, canvasWebviewConfig];

  if (isWatch) {
    const contexts = await Promise.all(
      configs.map(config => esbuild.context(config))
    );
    await Promise.all(contexts.map(ctx => ctx.watch()));
    console.log('[watch] Build started...');
  } else {
    await Promise.all(configs.map(config => esbuild.build(config)));
    console.log('[build] Complete');
  }
}

build().catch((err) => {
  console.error(err);
  process.exit(1);
});
