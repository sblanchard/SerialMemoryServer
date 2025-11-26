# SerialMemoryServer - CORE-like Knowledge Graph Memory System

A **temporal knowledge graph memory system** implementing the Model Context Protocol (MCP), inspired by [CORE (getcore.me)](https://getcore.me/).

> **Purpose**: Build your personal memory system to power your AI apps. Like CORE, but open-source, self-hosted, and built with PostgreSQL + pgvector for semantic search, entity extraction, relationship tracking, and multi-hop reasoning.

## ✨ Key Features

🧠 **Temporal Knowledge Graph** - Track who said what, when, and why with full provenance
🔍 **Semantic Search** - Find memories by meaning using embeddings (Ollama or ONNX)
🕸️ **Relationship Extraction** - Automatically extract entities and relationships
🎯 **Multi-Hop Reasoning** - Traverse knowledge graph connections for complex queries
👤 **User Personas** - Learn and recall user preferences, skills, and background
📊 **Event Sourcing** - Full audit trail with confidence decay and memory lifecycle
🏢 **Multi-Tenant** - Row-level security, usage metering, and tenant isolation
⚡ **Production Ready** - Rate limiting, circuit breakers, and comprehensive monitoring

## 🚀 Quick Start

### One-Command Setup (Recommended)

```bash
# Windows (PowerShell)
.\dev-bootstrap.ps1

# Linux/macOS
./dev-bootstrap.sh
```

This script starts all services and outputs configuration for Claude Desktop.

### Manual Setup

```bash
# Start infrastructure
docker compose -f docker-compose.dev.yml up -d

# Pull embedding model
docker exec serialmemory-ollama ollama pull nomic-embed-text

# Run MCP server
dotnet run --project SerialMemory.Mcp
```

📚 See [Local Development Guide](./docs/LOCAL_DEVELOPMENT.md) for detailed instructions.

## 📦 Client SDKs

Official client libraries with built-in retry, rate limiting, and circuit breaker:

### .NET SDK

```bash
dotnet add package SerialMemory.Client
```

```csharp
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "your-api-key"
});

// Ingest a memory
var result = await client.IngestAsync("John works at Acme Corp as an engineer.");

// Search for memories
var search = await client.SearchAsync("Who works at Acme?");
```

📚 [.NET SDK Documentation](./sdks/dotnet/SerialMemory.Client/README.md)

### Node.js / TypeScript SDK

```bash
npm install @serialmemory/client
```

```typescript
import { SerialMemoryClient } from '@serialmemory/client';

const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: 'your-api-key'
});

// Ingest a memory
const result = await client.ingest('John works at Acme Corp as an engineer.');

// Search for memories
const search = await client.search('Who works at Acme?');
```

📚 [Node.js SDK Documentation](./sdks/node/README.md)

## 📖 Examples

### [AI Second Brain](./examples/ai-second-brain/)

Use SerialMemory as a persistent "second brain" for AI assistants:
- Store notes, decisions, and learnings
- Semantic search for relevant context
- User persona management

```bash
cd examples/ai-second-brain/dotnet && dotnet run
```

### [Project Context Memory](./examples/project-context-memory/)

Isolated memory contexts per project:
- Multi-project memory isolation
- Cross-project search when needed
- IDE/editor integration patterns

```bash
cd examples/project-context-memory/node && npm start
```

## 🤖 Claude Desktop Integration

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "serialmemory": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp"],
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb",
        "OLLAMA_BASE_URL": "http://localhost:11434",
        "SERIALMEMORY_MODE": "self-hosted"
      }
    }
  }
}
```

### Available MCP Tools

| Tool | Description |
|------|-------------|
| `memory_search` | Search memories using semantic/text/hybrid search |
| `memory_ingest` | Add memories with automatic entity extraction |
| `memory_update` | Update memory content (creates new version) |
| `memory_delete` | Soft delete with audit trail |
| `memory_multi_hop_search` | Traverse knowledge graph for related context |
| `memory_about_user` | Retrieve user persona |
| `set_user_persona` | Set user preferences/skills/goals |
| `get_graph_statistics` | Knowledge graph stats |
| `detect_contradictions` | Find conflicting memories |
| `export_workspace` | Export all memories (JSON/encrypted) |

See [CLAUDE.md](./CLAUDE.md) for the full list of 33 MCP tools.

## 🏗️ Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    MCP Server (C#/STDIO)                      │
│         ┌────────────────────────────────────┐               │
│         │  Ollama embeddings (nomic-embed)   │               │
│         │  Entity extraction (patterns/LLM)  │               │
│         │  Event sourcing & CQRS            │               │
│         └────────────────────────────────────┘               │
│                         ↓                                     │
│         ┌────────────────────────────────────┐               │
│         │  PostgreSQL + pgvector             │               │
│         │  - memories (with embeddings)      │               │
│         │  - entities & relationships        │               │
│         │  - event store (audit trail)       │               │
│         │  - usage metering & rate limits    │               │
│         └────────────────────────────────────┘               │
└──────────────────────────────────────────────────────────────┘
```

### Components

| Component | Description |
|-----------|-------------|
| `SerialMemory.Mcp` | Main MCP server (C#, recommended) |
| `SerialMemory.Core` | Domain models and interfaces |
| `SerialMemory.Infrastructure` | PostgreSQL + pgvector implementation |
| `SerialMemory.EventSourcing` | Event sourcing with CQRS |
| `SerialMemory.Api` | REST API with SignalR (optional) |
| `SerialMemory.Api.Dashboard` | Tenant self-service API |

## 📊 Services (Development Stack)

| Service | URL | Description |
|---------|-----|-------------|
| PostgreSQL | `localhost:5432` | Database with pgvector |
| Ollama | `http://localhost:11434` | Local embeddings |
| Redis | `localhost:6379` | Caching & rate limiting |
| Prometheus | `http://localhost:9090` | Metrics |
| Grafana | `http://localhost:3001` | Dashboards |

## 🔬 SaaS Hardening (Phases 1-8 Complete)

Production-ready features for multi-tenant deployment:

- ✅ **Multi-Tenant Isolation** - Row-level security, tenant_id on all tables
- ✅ **JWT Authentication** - Scope-based access control
- ✅ **Usage Metering** - Credit-based billing, plan limits
- ✅ **Rate Limiting** - Per-tenant RPM limits with backoff
- ✅ **Admin Audit Log** - Tamper-evident hash chains
- ✅ **Abuse Protection** - Context size limits, input validation
- ✅ **Dashboard APIs** - Tenant self-service endpoints
- ✅ **Proof Endpoints** - Usage verification for billing

See [PLAN.md](./PLAN.md) for implementation details.

## 🛠️ Technology Stack

### Core
- **.NET 9** with C# 12
- **PostgreSQL 17 + pgvector** - Vector storage
- **Ollama** - Local embedding generation
- **Redis** - Caching and rate limiting

### SDKs
- **.NET SDK** - Full-featured with Polly resilience
- **Node.js SDK** - TypeScript with zero dependencies

### Observability
- **Prometheus** - Metrics collection
- **Grafana** - Dashboards
- **OpenTelemetry** - Distributed tracing

## 📈 Comparison with CORE

| Feature | CORE (getcore.me) | SerialMemory |
|---------|-------------------|--------------|
| Temporal knowledge graph | ✅ | ✅ |
| Semantic search | ✅ | ✅ (pgvector) |
| Entity extraction | ✅ | ✅ |
| Multi-hop reasoning | ✅ | ✅ |
| Event sourcing | ? | ✅ |
| Confidence decay | ? | ✅ |
| MCP integration | ✅ | ✅ |
| Client SDKs | ? | ✅ (.NET, Node.js) |
| Open source | ❌ | ✅ |
| Self-hosted | Paid | ✅ Free |

## 📚 Documentation

- [Local Development Guide](./docs/LOCAL_DEVELOPMENT.md) - Setup & configuration
- [CLAUDE.md](./CLAUDE.md) - Complete MCP tool reference
- [SDK Documentation](./sdks/) - Client library guides
- [Examples](./examples/) - Ready-to-run demo projects
- [PLAN.md](./PLAN.md) - SaaS hardening roadmap

## 🔧 Development

```bash
# Build solution
dotnet build SerialMemoryServer.sln

# Run tests
dotnet test

# Run MCP server with hot reload
dotnet watch run --project SerialMemory.Mcp
```

## 📄 License

MIT
