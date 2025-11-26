# What is SerialMemory?

SerialMemory is a **temporal knowledge graph memory system** for AI applications. Think of it as a persistent "second brain" that stores, indexes, and retrieves information using semantic search.

## Key Features

- **Semantic Search**: Find memories by meaning, not just keywords
- **Entity Extraction**: Automatically identifies people, organizations, dates, and concepts
- **Relationship Tracking**: Builds a knowledge graph of connections between entities
- **Multi-Hop Reasoning**: Traverses relationships to find related context
- **Event Sourcing**: Full audit trail of all changes with confidence decay
- **Multi-Tenant**: Isolated data per tenant with row-level security

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                         Client Applications                       │
│  (Claude Desktop, Custom Apps, SDKs)                             │
└──────────────────────────────┬───────────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────────┐
│                         API Layer                                 │
│  ┌─────────────┐  ┌──────────────────┐  ┌──────────────────┐    │
│  │ MCP Server  │  │ REST API         │  │ Dashboard API    │    │
│  │ (Claude)    │  │ (Custom Apps)    │  │ (Self-Service)   │    │
│  └─────────────┘  └──────────────────┘  └──────────────────┘    │
└──────────────────────────────┬───────────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────────┐
│                       Core Services                               │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  Knowledge Graph Service                                     │ │
│  │  - Memory ingestion with entity extraction                   │ │
│  │  - Semantic search (pgvector)                                │ │
│  │  - Multi-hop graph traversal                                 │ │
│  │  - User persona management                                   │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────┬───────────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────────┐
│                       Data Layer                                  │
│  ┌─────────────────┐  ┌──────────────┐  ┌─────────────────────┐ │
│  │ PostgreSQL      │  │ Ollama       │  │ Redis               │ │
│  │ + pgvector      │  │ (Embeddings) │  │ (Cache/Rate Limit)  │ │
│  └─────────────────┘  └──────────────┘  └─────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

## Core Concepts

### Memories

A memory is a piece of information stored in the system. Each memory has:
- **Content**: The actual text/information
- **Embedding**: A 384-dimensional vector for semantic search
- **Entities**: Extracted entities (people, organizations, etc.)
- **Confidence**: A score that decays over time unless reinforced
- **Layer**: Classification (L0_RAW through L4_HEURISTIC)

### Entities

Named things extracted from memories:
- **PERSON**: People's names
- **ORG**: Organizations and companies
- **GPE**: Geographic locations
- **DATE**: Dates and times
- **SKILL**: Technical skills and capabilities
- **PROJECT**: Project names

### Relationships

Connections between entities:
- "John" **works at** "Acme Corp"
- "Acme Corp" **located in** "San Francisco"
- "Project X" **uses** "Python"

### Multi-Hop Search

Starting from a query, the system can:
1. Find relevant memories
2. Extract entities from those memories
3. Find other memories linked to those entities
4. Repeat for N hops

This enables questions like "Who knows someone who works at Acme?" to be answered by traversing the knowledge graph.

## Use Cases

1. **AI Second Brain**: Personal knowledge management for AI assistants
2. **Project Context Memory**: Per-project isolated memory for development tools
3. **Customer Support**: Historical context for support conversations
4. **Research Assistants**: Persistent knowledge across research sessions

## Getting Started

- [Quick Start with Claude Desktop](./02-quickstart-claude-mcp.md)
- [.NET SDK Quick Start](./03-quickstart-dotnet-sdk.md)
- [Node.js SDK Quick Start](./04-quickstart-node-sdk.md)
- [Self-Hosting Guide](./07-self-hosting.md)
