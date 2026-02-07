import * as vscode from 'vscode';
import * as path from 'path';
import type { CodeFinding, AnalysisReport } from '../types/memory';
import type { McpClient } from './McpClient';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

const TODO_PATTERN = /\b(TODO|FIXME|HACK|XXX|BUG)\b[:\s]*(.*)/gi;
const LONG_FUNCTION_PATTERN = /(?:function\s+\w+|(?:async\s+)?(?:\w+\s*(?:=|:)\s*)?(?:\(.*?\)|[\w]+)\s*(?:=>|{))/g;

export class CodeAnalyzer {
  constructor(private readonly mcpClient?: McpClient) {}

  async analyzeWorkspace(scope?: string[]): Promise<AnalysisReport> {
    const start = Date.now();
    const patterns = scope ?? this.getAnalysisScope();
    const files = await this.findFiles(patterns);

    const allFindings: CodeFinding[] = [];

    for (const file of files) {
      try {
        const doc = await vscode.workspace.openTextDocument(file);
        const text = doc.getText();
        const lines = text.split('\n');
        const filePath = file.fsPath;

        allFindings.push(
          ...this.findTodoMarkers(filePath, lines),
          ...this.findLongFiles(filePath, lines),
          ...this.findLongFunctions(filePath, text, lines),
          ...this.findDeepNesting(filePath, lines),
          ...this.findBugPatterns(filePath, lines),
          ...this.findPerfIssues(filePath, lines),
          ...this.findQuickWins(filePath, lines),
        );
      } catch {
        // Skip files that can't be read
      }
    }

    // Sort by severity
    const severityOrder = { critical: 0, high: 1, medium: 2, low: 3 };
    allFindings.sort((a, b) => severityOrder[a.severity] - severityOrder[b.severity]);

    return {
      findings: allFindings,
      timestamp: new Date().toISOString(),
      filesScanned: files.length,
      duration: Date.now() - start,
    };
  }

  async ingestFindings(findings: CodeFinding[]): Promise<void> {
    if (!this.mcpClient?.isConnected) {
      return;
    }

    for (const finding of findings.filter(f => f.severity === 'critical' || f.severity === 'high')) {
      const content = `[${finding.type}] ${finding.title}\nFile: ${finding.file}:${finding.line}\n${finding.description}${finding.suggestion ? `\nSuggestion: ${finding.suggestion}` : ''}`;
      const memoryType = finding.type === 'bug' ? 'bugfix' : 'pattern';

      try {
        await this.mcpClient.ingest(content, 'serialtree-analyzer', memoryType, {
          finding_type: finding.type,
          severity: finding.severity,
          file: finding.file,
          line: finding.line,
        });
      } catch {
        // Best-effort ingestion
      }
    }
  }

  // --- Analyzers ---

  private findTodoMarkers(filePath: string, lines: string[]): CodeFinding[] {
    const findings: CodeFinding[] = [];

    for (let i = 0; i < lines.length; i++) {
      let match;
      TODO_PATTERN.lastIndex = 0;
      while ((match = TODO_PATTERN.exec(lines[i])) !== null) {
        const marker = match[1].toUpperCase();
        const detail = match[2]?.trim() ?? '';
        findings.push({
          type: 'tech_debt',
          severity: marker === 'FIXME' || marker === 'BUG' ? 'high' : 'medium',
          file: filePath,
          line: i + 1,
          title: `${marker} marker`,
          description: detail || `${marker} found without description`,
          suggestion: `Address and remove ${marker} marker`,
        });
      }
    }

    return findings;
  }

  private findLongFiles(filePath: string, lines: string[]): CodeFinding[] {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const maxLength = config.get<number>(CONFIG.MAX_FILE_LENGTH) ?? DEFAULTS.MAX_FILE_LENGTH;

    if (lines.length > maxLength) {
      return [{
        type: 'tech_debt',
        severity: lines.length > maxLength * 1.5 ? 'high' : 'medium',
        file: filePath,
        line: 1,
        title: `File too long (${lines.length} lines)`,
        description: `File exceeds ${maxLength} line limit. Consider splitting into smaller modules.`,
        suggestion: 'Extract related functionality into separate files',
      }];
    }

    return [];
  }

  private findLongFunctions(filePath: string, text: string, lines: string[]): CodeFinding[] {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const maxFnLength = config.get<number>(CONFIG.MAX_FUNCTION_LENGTH) ?? DEFAULTS.MAX_FUNCTION_LENGTH;
    const findings: CodeFinding[] = [];

    LONG_FUNCTION_PATTERN.lastIndex = 0;
    let match;
    while ((match = LONG_FUNCTION_PATTERN.exec(text)) !== null) {
      const startOffset = match.index;
      const startLine = text.substring(0, startOffset).split('\n').length;

      // Find matching brace
      let braceCount = 0;
      let foundOpen = false;
      let endLine = startLine;

      for (let i = startLine - 1; i < lines.length; i++) {
        for (const ch of lines[i]) {
          if (ch === '{') { braceCount++; foundOpen = true; }
          if (ch === '}') { braceCount--; }
          if (foundOpen && braceCount === 0) {
            endLine = i + 1;
            break;
          }
        }
        if (foundOpen && braceCount === 0) { break; }
      }

      const fnLength = endLine - startLine;
      if (fnLength > maxFnLength) {
        findings.push({
          type: 'tech_debt',
          severity: fnLength > maxFnLength * 2 ? 'high' : 'medium',
          file: filePath,
          line: startLine,
          title: `Long function (${fnLength} lines)`,
          description: `Function starting at line ${startLine} is ${fnLength} lines (max: ${maxFnLength}). Extract helper methods.`,
          suggestion: 'Extract logical blocks into smaller, named helper functions',
        });
      }
    }

    return findings;
  }

  private findDeepNesting(filePath: string, lines: string[]): CodeFinding[] {
    const findings: CodeFinding[] = [];
    let maxNesting = 0;
    let currentNesting = 0;
    let maxNestingLine = 0;

    for (let i = 0; i < lines.length; i++) {
      for (const ch of lines[i]) {
        if (ch === '{') { currentNesting++; }
        if (ch === '}') { currentNesting--; }
      }
      if (currentNesting > maxNesting) {
        maxNesting = currentNesting;
        maxNestingLine = i + 1;
      }
    }

    if (maxNesting > 4) {
      findings.push({
        type: 'tech_debt',
        severity: maxNesting > 6 ? 'high' : 'medium',
        file: filePath,
        line: maxNestingLine,
        title: `Deep nesting (${maxNesting} levels)`,
        description: `Nesting depth of ${maxNesting} at line ${maxNestingLine}. Max recommended: 4.`,
        suggestion: 'Use early returns, extract methods, or invert conditions to reduce nesting',
      });
    }

    return findings;
  }

  private findBugPatterns(filePath: string, lines: string[]): CodeFinding[] {
    const findings: CodeFinding[] = [];
    const ext = path.extname(filePath);

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      // Unhandled promise (JS/TS)
      if ((ext === '.ts' || ext === '.js' || ext === '.tsx') && /(?:^|\s)(?:await\s+)?[\w.]+\(/.test(line) && /\.then\(/.test(line) && !line.includes('.catch')) {
        findings.push({
          type: 'bug',
          severity: 'medium',
          file: filePath,
          line: i + 1,
          title: 'Unhandled promise rejection',
          description: '.then() without .catch() — rejected promise will be silently swallowed',
          suggestion: 'Add .catch() or use try/await',
        });
      }

      // Type assertion abuse (TS)
      if (ext === '.ts' || ext === '.tsx') {
        const asAnyMatches = line.match(/as\s+any/g);
        if (asAnyMatches && asAnyMatches.length > 0) {
          findings.push({
            type: 'bug',
            severity: 'low',
            file: filePath,
            line: i + 1,
            title: '"as any" type assertion',
            description: 'Bypasses TypeScript type safety. May hide real type errors.',
            suggestion: 'Use proper type narrowing or define the correct type',
          });
        }
      }
    }

    return findings;
  }

  private findPerfIssues(filePath: string, lines: string[]): CodeFinding[] {
    const findings: CodeFinding[] = [];

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      // N+1 pattern: await inside for loop
      if (/for\s*\(/.test(line) || /\.forEach\(/.test(line)) {
        // Look ahead for await within the loop body
        let braceDepth = 0;
        for (let j = i; j < Math.min(i + 30, lines.length); j++) {
          for (const ch of lines[j]) {
            if (ch === '{') { braceDepth++; }
            if (ch === '}') { braceDepth--; }
          }
          if (braceDepth > 0 && /\bawait\b/.test(lines[j]) && j > i) {
            findings.push({
              type: 'performance',
              severity: 'high',
              file: filePath,
              line: i + 1,
              title: 'Potential N+1 query',
              description: `await inside loop at line ${j + 1}. Each iteration waits sequentially.`,
              suggestion: 'Use Promise.all() or batch the operations',
            });
            break;
          }
          if (braceDepth <= 0 && j > i) { break; }
        }
      }

      // String concatenation in loop
      if (/\bfor\b/.test(line) && i + 5 < lines.length) {
        for (let j = i + 1; j < Math.min(i + 15, lines.length); j++) {
          if (/\+=\s*['"`]/.test(lines[j]) || /\+=\s*\w+/.test(lines[j])) {
            findings.push({
              type: 'performance',
              severity: 'low',
              file: filePath,
              line: j + 1,
              title: 'String concatenation in loop',
              description: 'Repeated string concatenation creates intermediate strings.',
              suggestion: 'Use array.join() or template literals',
            });
            break;
          }
        }
      }
    }

    return findings;
  }

  private findQuickWins(filePath: string, lines: string[]): CodeFinding[] {
    const findings: CodeFinding[] = [];

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      // Console.log left in code
      if (/\bconsole\.log\b/.test(line) && !line.trimStart().startsWith('//')) {
        findings.push({
          type: 'quick_win',
          severity: 'low',
          file: filePath,
          line: i + 1,
          title: 'console.log statement',
          description: 'Debug logging left in production code',
          suggestion: 'Remove or replace with proper logging',
        });
      }

      // Unused imports (basic detection for TS/JS)
      if (/^import\s+.*\bfrom\b/.test(line)) {
        const importMatch = line.match(/import\s+(?:type\s+)?{?\s*([\w,\s*]+)\s*}?\s+from/);
        if (importMatch) {
          const imports = importMatch[1].split(',').map(s => s.trim()).filter(s => s && s !== '*');
          for (const imp of imports) {
            const name = imp.replace(/\s+as\s+\w+/, '').trim();
            if (!name) { continue; }
            // Quick check: does name appear elsewhere in file?
            const usageCount = lines.filter((l, idx) => idx !== i && l.includes(name)).length;
            if (usageCount === 0) {
              findings.push({
                type: 'quick_win',
                severity: 'low',
                file: filePath,
                line: i + 1,
                title: `Unused import: ${name}`,
                description: `'${name}' is imported but never referenced in this file`,
                suggestion: `Remove the unused import of '${name}'`,
              });
            }
          }
        }
      }
    }

    return findings;
  }

  // --- Helpers ---

  private getAnalysisScope(): string[] {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    return config.get<string[]>(CONFIG.ANALYSIS_SCOPE) ?? [...DEFAULTS.ANALYSIS_SCOPE];
  }

  private async findFiles(patterns: string[]): Promise<vscode.Uri[]> {
    const allFiles: vscode.Uri[] = [];

    for (const pattern of patterns) {
      const files = await vscode.workspace.findFiles(pattern, '**/node_modules/**', 500);
      allFiles.push(...files);
    }

    // Deduplicate
    const seen = new Set<string>();
    return allFiles.filter(f => {
      if (seen.has(f.fsPath)) { return false; }
      seen.add(f.fsPath);
      return true;
    });
  }
}
