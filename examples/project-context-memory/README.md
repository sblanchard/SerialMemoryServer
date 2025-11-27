# Project Context Memory Example

A minimal example demonstrating how to use SerialMemory for project-specific memory isolation. This is ideal for development tools, IDEs, or AI assistants that work across multiple projects.

## What This Example Does

1. **Demonstrates project isolation:**
   - Each project gets its own memory context
   - Searches are scoped to the current project
   - No cross-contamination between projects

2. **Shows practical patterns:**
   - Architecture Decision Records (ADRs)
   - Bug fix documentation
   - Code patterns and conventions
   - Cross-project search when needed

3. **Multi-tenant simulation:**
   - Similar to how SaaS deployments isolate tenants
   - Uses metadata and source filtering

## Quick Start

### Prerequisites

- SerialMemory running locally (see [Local Development Guide](../../docs/LOCAL_DEVELOPMENT.md))
- .NET 8 SDK or Node.js 18+

### Run with .NET

```bash
cd dotnet
dotnet run
```

### Run with Node.js

```bash
cd node
npm install
npm start
```

## Key Concepts

### 1. Project Scoping with Metadata

Each memory is tagged with project identifiers:

```javascript
await client.ingest(`[${projectName}] ${content}`, {
  source: `project-${projectId}`,
  metadata: {
    projectId: projectId,
    projectName: projectName,
    category: 'bug-fix',
    timestamp: new Date().toISOString()
  }
});
```

### 2. Project-Scoped Search

Search includes project context for better relevance:

```javascript
async search(query, limit = 5) {
  // Add project context to query
  const contextualQuery = `${this.projectName} ${query}`;

  const results = await this.client.search(contextualQuery, {
    mode: 'hybrid',
    limit: limit * 2
  });

  // Filter to this project only
  return results.memories.filter(m =>
    m.source === `project-${this.projectId}` ||
    m.content.startsWith(`[${this.projectName}]`)
  ).slice(0, limit);
}
```

### 3. Cross-Project Search

When you need to find patterns across all projects:

```javascript
// Direct client search without project filtering
const globalResults = await client.search('code patterns best practices', {
  mode: 'hybrid',
  limit: 10
});
```

## Use Cases

### 1. IDE/Editor Integration

```javascript
class EditorMemory {
  constructor(client) {
    this.client = client;
    this.projectManager = null;
  }

  onProjectOpened(projectPath) {
    const projectId = hashPath(projectPath);
    const projectName = path.basename(projectPath);
    this.projectManager = new ProjectMemoryManager(
      this.client,
      projectId,
      projectName
    );
  }

  async getContextForFile(filePath) {
    return this.projectManager.search(`file:${filePath}`);
  }

  async recordBugFix(issue, solution, files) {
    return this.projectManager.storeBugFix(issue, '', solution, files);
  }
}
```

### 2. Multi-Repo Development Tool

```javascript
// User is working on microservices
const authService = new ProjectMemoryManager(client, 'auth-service', 'Auth Service');
const apiGateway = new ProjectMemoryManager(client, 'api-gateway', 'API Gateway');
const userService = new ProjectMemoryManager(client, 'user-service', 'User Service');

// Each service has isolated memory
await authService.storeDecision('JWT over sessions', ...);
await apiGateway.storeDecision('Rate limiting at gateway', ...);

// But can search across all when needed
const crossServiceResults = await client.search('authentication flow');
```

### 3. AI Coding Assistant

```javascript
class CodingAssistant {
  constructor(memoryClient) {
    this.memory = memoryClient;
    this.currentProject = null;
  }

  // Called when context switches
  setProject(projectId, projectName) {
    this.currentProject = new ProjectMemoryManager(
      this.memory, projectId, projectName
    );
  }

  // Get relevant context before answering
  async getContext(userQuery) {
    const projectContext = await this.currentProject.search(userQuery);
    const globalPatterns = await this.memory.search(
      `best practice ${userQuery}`,
      { limit: 2 }
    );
    return { projectContext, globalPatterns };
  }

  // Store learnings from the interaction
  async recordLearning(topic, insight) {
    await this.currentProject.store(
      `Learning: ${topic}\n\n${insight}`,
      'learning'
    );
  }
}
```

## Memory Categories

Organize memories by type for each project:

| Category | Description | Example Content |
|----------|-------------|-----------------|
| `architecture-decision` | Technical choices with rationale | "Use GraphQL for API" |
| `bug-fix` | Root cause analyses | "Memory leak in useEffect" |
| `code-pattern` | Conventions and patterns | "Repository pattern usage" |
| `learning` | Insights and discoveries | "pgvector index selection" |
| `todo` | Tasks and reminders | "Refactor auth module" |

## Best Practices

### 1. Consistent Project IDs

Use stable identifiers that persist across sessions:

```javascript
// Good: Based on repo root or project config
const projectId = crypto.createHash('sha256')
  .update(projectPath)
  .digest('hex')
  .slice(0, 12);

// Bad: Random or session-based
const projectId = Math.random().toString(36);
```

### 2. Meaningful Project Names

Names appear in search results and logs:

```javascript
// Good: Clear, unique names
const projectName = 'Acme E-commerce Backend';

// Bad: Generic names
const projectName = 'Project 1';
```

### 3. Category Consistency

Use the same categories across projects:

```javascript
const CATEGORIES = {
  DECISION: 'architecture-decision',
  BUG_FIX: 'bug-fix',
  PATTERN: 'code-pattern',
  LEARNING: 'learning'
};
```

### 4. Content Structure

Structured content improves search relevance:

```
Bug Fix: [Brief title]

Issue: [What was wrong]

Root Cause:
[Why it happened]

Solution:
[How it was fixed]

Affected Files:
- file1.ts
- file2.ts
```

## Example Output

```
🗂️ Project Context Memory Demo
================================

📦 Setting up two projects with isolated memory contexts...

Project: E-commerce Backend
----------------------------
✅ Stored ADR: Payment processor selection
✅ Stored bug fix: Cart calculation issue
✅ Stored code pattern: Repository Pattern

Project: Mobile App
--------------------
✅ Stored ADR: Framework selection
✅ Stored bug fix: Tab navigation crash
✅ Stored code pattern: Custom Hook Pattern

🔍 Demonstrating project-scoped search...

Query: 'payment' in E-commerce Backend:
   [87%] [E-commerce Backend] Architecture Decision: Use Stripe for payments

Query: 'payment' in Mobile App:
   (No results - payment decisions are in E-commerce project)

🌐 Cross-project search (all projects)...

Query: 'code patterns best practices' (all projects):
   [E-commerce Backend] [82%] Code Pattern: Repository Pattern
   [Mobile App] [78%] Code Pattern: Custom Hook Pattern
```

## Files

```
project-context-memory/
├── README.md           # This file
├── dotnet/
│   ├── Program.cs      # .NET implementation
│   └── *.csproj        # Project file
└── node/
    ├── index.js        # Node.js implementation
    └── package.json    # Dependencies
```

## Related Examples

- [AI Second Brain](../ai-second-brain/) - Personal memory without project isolation
- [SDK Examples](../../sdks/) - Lower-level SDK usage patterns
