# SerialMemory Examples

Ready-to-run example projects demonstrating SerialMemory integration patterns.

## Quick Start

1. **Start SerialMemory locally:**
   ```bash
   ./dev-bootstrap.sh      # Linux/Mac
   .\dev-bootstrap.ps1     # Windows
   ```

2. **Run an example:**
   ```bash
   # .NET
   cd examples/ai-second-brain/dotnet
   dotnet run

   # Node.js
   cd examples/ai-second-brain/node
   npm install && npm start
   ```

## Available Examples

### [AI Second Brain](./ai-second-brain/)

A persistent "second brain" for AI assistants. Demonstrates:
- Storing notes, decisions, and learnings
- Semantic search for relevant context
- User persona management
- Multi-hop knowledge graph traversal

**Use cases:** Personal AI assistant, knowledge management, decision logging

### [Project Context Memory](./project-context-memory/)

Project-specific memory isolation. Demonstrates:
- Scoped memory per project/repository
- Metadata-based filtering
- Cross-project search when needed
- Multi-tenant patterns

**Use cases:** IDE integration, development tools, multi-repo AI assistants

## SDK Examples

Lower-level SDK usage examples are in the [sdks/](../sdks/) directory:

- [.NET SDK Examples](../sdks/dotnet/examples/)
- [Node.js SDK Examples](../sdks/node/examples/)

## Common Patterns

### Memory Content Structure

Well-structured content improves search relevance:

```
[Category]: [Brief title]

Context:
[Background information]

Details:
[Main content]

Tags: tag1, tag2
Date: YYYY-MM-DD
```

### Using Metadata

```javascript
await client.ingest(content, {
  source: 'my-app',
  metadata: {
    type: 'decision',
    project: 'acme',
    importance: 'high',
    tags: ['architecture', 'database']
  }
});
```

### Error Handling

```javascript
try {
  const result = await client.search(query);
} catch (error) {
  if (error instanceof RateLimitExceededError) {
    await sleep(error.retryAfter);
    // retry
  } else if (error instanceof UsageLimitExceededError) {
    console.log('Upgrade plan or wait for reset');
  }
}
```

## Environment Variables

All examples support these environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `SERIALMEMORY_URL` | `http://localhost:5000` | API base URL |
| `SERIALMEMORY_API_KEY` | `demo-key` | API key for authentication |

## Need Help?

- [Local Development Guide](../docs/LOCAL_DEVELOPMENT.md)
- [SDK Documentation](../sdks/)
- [Main README](../README.md)
