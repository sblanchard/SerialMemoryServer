# AI Second Brain Example

A minimal example demonstrating how to use SerialMemory as a persistent "second brain" for an AI assistant. This example shows how to store and retrieve long-term notes, decisions, and insights.

## What This Example Does

1. **Stores various types of memories:**
   - Personal notes and observations
   - Technical decisions with rationale
   - Learnings from debugging sessions
   - Meeting notes and action items

2. **Retrieves relevant context:**
   - Semantic search for related memories
   - Multi-hop search through entity relationships
   - User persona for preferences and skills

3. **Demonstrates memory patterns:**
   - How to structure memory content for best retrieval
   - Using metadata for categorization
   - Building up knowledge over multiple sessions

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

### 1. Structured Memory Content

Good memory content is specific and includes context:

```
Decision: Use PostgreSQL with pgvector for the project
Rationale: Need vector similarity search for semantic retrieval
Alternatives considered: Pinecone (cost), Qdrant (operational complexity)
Date: 2024-01-15
```

### 2. Using Metadata

Metadata enables filtering and categorization:

```javascript
await client.ingest(content, {
  source: 'architecture-decisions',
  metadata: {
    type: 'decision',
    project: 'acme-app',
    importance: 'high',
    tags: ['database', 'vector-search']
  }
});
```

### 3. Multi-Hop Discovery

Find related information through entity connections:

```javascript
// Find everything related to "Acme Corp"
const results = await client.multiHopSearch('Acme Corp', { hops: 2 });

// This finds:
// - Direct mentions of Acme Corp
// - People who work at Acme Corp
// - Projects involving those people
// - Technologies used in those projects
```

### 4. User Persona

Build up knowledge about the user over time:

```javascript
// Record skills as they're discovered
await client.setUserPersona('skill', 'frontend', 'React, Vue, TypeScript');

// Record preferences
await client.setUserPersona('preference', 'testing', 'Prefer integration tests over unit tests');

// Later, retrieve persona to personalize responses
const persona = await client.getUserPersona();
```

## Example Output

```
🧠 AI Second Brain Demo
======================

📝 Storing memories...
✅ Stored decision: 550e8400-e29b-41d4-a716-446655440000
   Extracted entities: PostgreSQL, pgvector, Pinecone, Qdrant
✅ Stored learning: 550e8400-e29b-41d4-a716-446655440001
✅ Stored meeting note: 550e8400-e29b-41d4-a716-446655440002

👤 Building user persona...
✅ Set skill: programming_languages = Python, TypeScript, C#
✅ Set preference: communication = concise and technical
✅ Set goal: current_focus = Ship MVP by end of month

🔍 Searching for context...
Query: "What database should we use?"

Found 3 relevant memories:
📄 [89% match] Decision: Use PostgreSQL with pgvector...
📄 [72% match] Learning: When configuring pgvector...
📄 [65% match] Meeting note: Database discussion with team...

🔗 Multi-hop search for "John Smith"...
Direct matches: 2
Related via entity graph:
  Hop 1 via 'Acme Corp': 3 memories
  Hop 2 via 'React Native': 2 memories
```

## Memory Categories

Consider organizing memories by type:

| Category | Source Tag | Use Case |
|----------|------------|----------|
| `architecture-decisions` | ADRs, tech choices | Long-term reference |
| `bug-fixes` | Root cause analyses | Prevent repeat issues |
| `meeting-notes` | Action items, discussions | Follow-up tracking |
| `learnings` | Debugging insights, tips | Knowledge capture |
| `personal-preferences` | Preferences, styles | Personalization |

## Best Practices

1. **Be Specific**: "Use PostgreSQL" is less useful than "Use PostgreSQL for its JSON support and pgvector extension"

2. **Include Context**: Why was this decision made? What alternatives were considered?

3. **Add Dates**: Temporal context helps with relevance and decay

4. **Use Consistent Structure**: Makes retrieval more predictable

5. **Don't Over-Store**: Focus on insights and decisions, not raw data

## Files

```
ai-second-brain/
├── README.md           # This file
├── dotnet/
│   ├── Program.cs      # .NET implementation
│   └── *.csproj        # Project file
└── node/
    ├── index.js        # Node.js implementation
    └── package.json    # Dependencies
```

## Related Examples

- [Project Context Memory](../project-context-memory/) - Multi-project isolation
- [SDK Examples](../../sdks/) - More SDK usage patterns
