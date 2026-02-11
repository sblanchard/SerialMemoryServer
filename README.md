# SerialMemory

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://img.shields.io/badge/build-passing-brightgreen?logo=github)](https://github.com/sblanchard/SerialMemoryServer/actions)
[![.NET Version](https://img.shields.io/badge/.NET-10+-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-15+-336791?logo=postgresql)](https://www.postgresql.org)
[![Docker Ready](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker)](https://www.docker.com)
[![Code Coverage](https://img.shields.io/badge/coverage-82%25-brightgreen?logo=codecov)](./docs/)
[![Activity](https://img.shields.io/badge/maintenance-active-green?logo=github)](https://github.com/sblanchard/SerialMemoryServer)

[![Semantic Search](https://img.shields.io/badge/Semantic%20Search-pgvector-FF6B6B?logo=postgresql&logoColor=white)](./docs/01-overview.md)
[![Knowledge Graph](https://img.shields.io/badge/Knowledge%20Graph-Multi%20Hop-4ECDC4?logo=neo4j&logoColor=white)](./docs/01-overview.md)
[![MCP Protocol](https://img.shields.io/badge/MCP%20Protocol-Enabled-9B59B6?logo=aiohttp&logoColor=white)](./docs/02-quickstart-claude-mcp.md)
[![Multi-Tenant](https://img.shields.io/badge/Multi%20Tenant-Secure-27AE60?logo=security&logoColor=white)](./docs/07-self-hosting.md)

[![Platform: Linux](https://img.shields.io/badge/Platform-Linux-FCC624?logo=linux&logoColor=black)](https://github.com/sblanchard/SerialMemoryServer)
[![Platform: macOS](https://img.shields.io/badge/Platform-macOS-000000?logo=apple&logoColor=white)](https://github.com/sblanchard/SerialMemoryServer)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows&logoColor=white)](https://github.com/sblanchard/SerialMemoryServer)

[![Issues](https://img.shields.io/github/issues/sblanchard/SerialMemoryServer?logo=github)](https://github.com/sblanchard/SerialMemoryServer/issues)
[![Pull Requests](https://img.shields.io/github/issues-pr/sblanchard/SerialMemoryServer?logo=github)](https://github.com/sblanchard/SerialMemoryServer/pulls)
[![Stars](https://img.shields.io/github/stars/sblanchard/SerialMemoryServer?logo=github&style=flat&label=Stars)](https://github.com/sblanchard/SerialMemoryServer)
[![Contributors](https://img.shields.io/github/contributors/sblanchard/SerialMemoryServer?logo=github)](https://github.com/sblanchard/SerialMemoryServer/graphs/contributors)

</div>

A **temporal knowledge graph memory system** for AI applications. SerialMemory provides persistent "second brain" capabilities with semantic search, entity extraction, and relationship tracking—enabling AI agents to maintain context and learn from past interactions.

## 🎯 Features

- **Semantic Search**: Find memories by meaning, not keywords, using vector embeddings (384-dim)
- **Entity Extraction**: Automatically identify people, organizations, locations, skills, and projects
- **Knowledge Graph**: Build and traverse relationships between entities for multi-hop reasoning
- **Temporal Tracking**: Full event sourcing with confidence decay and audit trails
- **Multi-Tenant**: Complete data isolation with row-level security
- **MCP Integration**: Claude Desktop and future AI agents via Model Context Protocol
- **REST API**: Full-featured HTTP API for custom integrations
- **Self-Hosted**: Deploy on your own infrastructure with security hardening

## 🏗️ Architecture

```
┌────────────────────────────────────────────┐
│      Client Applications                    │
│  (Claude Desktop, Custom Apps, SDKs)       │
└────────────────┬─────────────────────────┘
                 │
         ┌───────┴────────┬──────────────┐
         ▼                ▼              ▼
    ┌─────────┐  ┌──────────────┐  ┌────────┐
    │   MCP   │  │  REST API    │  │ Admin  │
    │ Server  │  │  (HTTP)      │  │ API    │
    └────┬────┘  └──────┬───────┘  └───┬────┘
         │               │              │
         └───────────────┼──────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │   Core Services               │
         │  ┌─────────────────────────┐  │
         │  │ Knowledge Graph Engine  │  │
         │  │ • Memory ingestion      │  │
         │  │ • Entity extraction     │  │
         │  │ • Semantic search       │  │
         │  │ • Graph traversal       │  │
         │  └─────────────────────────┘  │
         └───────────────┬───────────────┘
                         │
        ┌────────────────┼────────────────┐
        ▼                ▼                ▼
    ┌──────────┐   ┌───────────┐   ┌─────────┐
    │PostgreSQL│   │  Ollama   │   │  Redis  │
    │+pgvector │   │(Embeddings)│   │(Cache)  │
    └──────────┘   └───────────┘   └─────────┘
```

## 🧠 Core Concepts

### How It Works: A Temporal Knowledge Graph

SerialMemory goes beyond simple keyword search. It's a **semantic memory system** that understands meaning and context, tracks how information relates over time, and provides intelligent context retrieval for AI applications.

### Memories: The Basic Unit

A **memory** is any piece of information you store in SerialMemory:

- **Natural Language**: "John works at Acme Corp in San Francisco on Python projects"
- **Structured Data**: Notes, facts, observations, conversations, summaries
- **Metadata**: When it was created, what project it's for, confidence level

Each memory has:

```
Memory {
  id: "uuid",
  content: "John works at Acme Corp in San Francisco on Python projects",
  embedding: [0.123, -0.456, 0.789, ...],  // 384-dimensional vector
  confidence: 0.95,                         // Starts high, decays with time
  layer: L1_CONTEXT,                        // See layers section below
  entities: [
    { type: "PERSON", text: "John" },
    { type: "ORG", text: "Acme Corp" },
    { type: "GPE", text: "San Francisco" },
    { type: "SKILL", text: "Python" }
  ],
  createdAt: "2026-02-11T10:00:00Z",
  lastAccessedAt: "2026-02-11T14:30:00Z"
}
```

### Semantic Search: Understanding Meaning

Instead of keyword matching, memories are found by **semantic similarity**:

```
// This query...
"Who works at the tech company in San Francisco?"

// ...finds memories about "John works at Acme Corp in San Francisco"
// Even though keywords don't match exactly!
```

**How it works:**
1. Your query is converted to an embedding (384-dimensional vector)
2. The system finds memories with similar embeddings
3. Results are ranked by meaning, not keywords

This is powered by PostgreSQL's `pgvector` extension, which uses vector similarity search (KNN) for fast retrieval.

### Memory Layers: The Data Lifecycle

Memories are classified into layers based on how processed/refined they are:

| Layer | What It Is | Typical Retention | Use Case |
|-------|-----------|-------------------|----------|
| **L0_RAW** | Raw input, minimal processing | 30 days | Recently captured notes, conversations |
| **L1_CONTEXT** | Contextual understanding, entities extracted | 90 days | Processed notes with relationships identified |
| **L2_SUMMARY** | Summarized information, themes extracted | 180 days | Synthesis of similar concepts |
| **L3_KNOWLEDGE** | Extracted knowledge, proven patterns | 365 days | Reliable facts, validated learnings |
| **L4_HEURISTIC** | Learned patterns, general principles | Indefinite | Core knowledge, fundamental truths |

**Example Flow:**
```
Raw: "John, Sarah, and Mike met yesterday to discuss the backend API design"
      ↓ (Processing)
L1: Entities extracted - PERSON(John), PERSON(Sarah), PERSON(Mike), 
    Relationships identified - John knows Sarah, John knows Mike
      ↓ (Summarization)
L2: "Key discussion participants coordinated on technical architecture"
      ↓ (Pattern recognition)
L3: "Backend API design is a priority for team coordination"
      ↓ (Generalization)
L4: "The team uses synchronous discussion for architectural decisions"
```

### Memory Confidence Decay: Temporal Forgetting

SerialMemory models human memory: information becomes less reliable over time unless reinforced.

**The decay formula:**
```
confidence = initial_confidence × 0.5^(days / half_life)

Default half-life: 90 days
```

**Example:**
```
Day 0:   Confidence = 1.0 (100%)
Day 90:  Confidence = 0.5 (50%)  - Half as confident
Day 180: Confidence = 0.25 (25%) - Quarter as confident
Day 270: Confidence = 0.125 (12.5%)
```

**Why this matters:**
- New information is fresh and high-confidence
- Old information without reinforcement becomes less trusted
- Memories below 0.1 (10%) confidence are candidates for archival
- Accessing a memory reinforces it (increases confidence)
- An explicit "reinforce" action can boost confidence

This allows the system to **automatically forget unimportant details** while keeping validated knowledge intact.

### Entity Extraction: Understanding Key Concepts

SerialMemory automatically identifies key entities (named things) in memories:

| Type | Examples | Used For |
|------|----------|----------|
| **PERSON** | John, Sarah, Alice | Finding people-related memories |
| **ORG** | Acme Corp, Google, MIT | Finding organization memories |
| **GPE** | New York, France, Silicon Valley | Finding location-related context |
| **DATE** | 2026-02, Next Tuesday, Q1 | Finding time-relevant memories |
| **SKILL** | Python, React, Leadership | Finding capability-related memories |
| **PROJECT** | Project Omega, Dashboard v2 | Finding project-specific context |

**How it works:**
1. When a memory is stored, entities are automatically extracted
2. Relationships between entities are identified
3. A knowledge graph is built and maintained

### The Knowledge Graph: Building Relationships

SerialMemory doesn't just store memories separately—it builds a **knowledge graph** of relationships:

```
John ─────works_at────→ Acme Corp ─────located_in────→ San Francisco
  │                                                          │
  │                                                          │
  └──────knows──────→ Sarah ─────works_at──────→ Acme Corp │
                       │
                       └─────knows──────→ Mike

Projects that use Python:
Dashboard v2 ─────uses────→ Python ─────language_for────→ Backend Systems
Project Omega ─────uses────→ Python
```

**Relationships tracked:**
- `works_at`: Entity1 works at Entity2
- `located_in`: Entity1 is located in Entity2
- `knows`: Entity1 knows Entity2
- `uses`: Entity1 uses Entity2
- `created`: Entity1 created Entity2
- (And many more, automatically discovered)

### Multi-Hop Search: Reasoning Across Context

The real power emerges when traversing multiple hops:

**Query:** "Find technologies used in projects by people I know"

**Search process:**
```
1. Start: Find entities I know (Sarah, Mike, ...)
2. Hop 1: Find projects they work on
3. Hop 2: Find technologies used in those projects
4. Result: [Python, Rust, TypeScript, ...]
```

**Another example:**
```
Question: "Who works with technologies similar to what Mike uses?"

1. Find Mike
2. Find technologies Mike uses
3. Find other people who use similar technologies
4. Return those people

This reveals hidden connections and provides better context
for multi-modal AI reasoning!
```

### Event Sourcing: Complete Audit Trail

Every change to a memory is captured as an immutable event:

```
MemoryCreated(id, content, timestamp)
MemoryUpdated(id, oldContent, newContent, timestamp)
MemoryReinforced(id, confidence, timestamp)
MemoryDecayed(id, oldConfidence, newConfidence, timestamp)
MemoryArchived(id, reason, timestamp)
```

**Benefits:**
- Complete audit trail (GDPR compliant)
- Reproduce system state at any point in time
- Detect tampering or unauthorized changes
- Replay events for debugging

### Multi-Tenant Isolation: Security & Privacy

Each tenant's data is completely isolated:

```
Tenant A                          Tenant B
├── Memories (isolated)           ├── Memories (isolated)
├── Entities                      ├── Entities
├── Relationships                 ├── Relationships
└── Search results (A only)       └── Search results (B only)
```

Implemented via PostgreSQL Row-Level Security (RLS):
- Database enforces isolation at query time
- Impossible to leak tenant data via SQL
- API keys scoped per tenant
- No cross-tenant search possible

## 🚀 Quick Start

### Prerequisites

- Docker & Docker Compose
- .NET 10+ SDK (for development)
- PostgreSQL 15+ (or use Docker)

### Option 1: Docker Compose (Recommended)

```bash
# Clone the repository
git clone https://github.com/sblanchard/SerialMemoryServer.git
cd SerialMemoryServer

# Start infrastructure (PostgreSQL, Redis, RabbitMQ)
docker compose up -d

# Bootstrap development database
./dev-bootstrap.sh          # Linux/Mac
.\dev-bootstrap.ps1         # Windows

# Run the C# MCP server
dotnet run --project SerialMemory.Mcp
```

### Option 2: Full Stack with Docker

```bash
# Start all services
docker compose up --build

# Access dashboard at http://localhost:8080
```

Services available:
- **API**: http://localhost:5001
- **Dashboard**: http://localhost:8080
- **PostgreSQL**: localhost:5432 (user: `postgres`)
- **Redis**: localhost:6379
- **RabbitMQ Dashboard**: http://localhost:15672 (guest/guest)

## 📚 Documentation

- [Overview & Architecture](./docs/01-overview.md) - Concepts and system design
- [Claude Desktop Setup](./docs/02-quickstart-claude-mcp.md) - MCP integration guide
- [.NET SDK Guide](./docs/03-quickstart-dotnet-sdk.md) - C# development
- [Node.js SDK Guide](./docs/04-quickstart-node-sdk.md) - JavaScript/TypeScript
- [Data Lifecycle](./docs/06-data-lifecycle.md) - Memory management and cleanup
- [Self-Hosting Guide](./docs/07-self-hosting.md) - VPS deployment
- [Local Development](./docs/LOCAL_DEVELOPMENT.md) - Development environment setup

## 💻 SDKs & Integrations

### Official SDKs

- **[.NET SDK](./SerialMemory.Sdk.DotNet/)** - Full-featured C# client
  ```csharp
  var client = new SerialMemoryClient("http://localhost:5001", "tenant-id", "api-key");
  await client.StoreMemory("My important note", "personal");
  var results = await client.Search("relevant memories", 5);
  ```

- **[Node.js SDK](./sdks/node/)** - TypeScript/JavaScript support (coming soon)

### MCP Protocol

SerialMemory implements the **Model Context Protocol**, enabling integration with:
- Claude Desktop
- Future compatible AI agents
- Custom MCP clients

## 🔌 API Endpoints

### Memory Management

```bash
# Store a memory
POST /api/memories
{
  "content": "I learned about distributed systems",
  "memoryType": "LEARNING",
  "layer": "L0_RAW",
  "metadata": { "subject": "backend" }
}

# Semantic search
POST /api/memories/search
{
  "query": "distributed systems",
  "limit": 5,
  "filters": { "layer": "L0_RAW" }
}

# Get memory by ID
GET /api/memories/{id}
```

### Entity & Knowledge Graph

```bash
# List entities
GET /api/entities?limit=100

# Get entity details
GET /api/entities/{entityId}

# Multi-hop graph search
POST /api/graph/search
{
  "startEntity": "John",
  "relationshipType": "knows",
  "hops": 2
}
```

See [OpenAPI Specification](./openapi.json) for complete API documentation.

## 📦 Project Structure

```
SerialMemoryServer/
├── SerialMemory.Mcp/           # C# MCP server (recommended)
├── SerialMemory.Api/           # REST API
├── SerialMemory.Core/          # Domain logic
├── SerialMemory.Infrastructure/# Data access layer
├── SerialMemory.Sdk.DotNet/    # .NET client SDK
├── SerialMemory.ML/            # Embeddings & NLP
├── SerialMemory.Worker/        # Background jobs
├── SerialMemory.Web/           # React dashboard
├── SerialMemory-MCP/           # Standalone MCP server (submodule)
│   └── SerialMemory.Mcp/       # Alternative MCP implementation
├── examples/                   # Ready-to-run examples
├── docs/                       # Documentation
├── docker-compose.yml          # Development stack
└── SerialMemoryServer.sln      # .NET solution file
```

### Submodules

- **[SerialMemory-MCP](https://github.com/sblanchard/SerialMemory-MCP)** - Standalone MCP server implementation
  - Included as a git submodule
  - Can be built and distributed separately
  - Full MCP protocol support for Claude Desktop and compatible agents

### Cloning with Submodules

```bash
# Clone including submodules
git clone --recurse-submodules https://github.com/sblanchard/SerialMemoryServer.git

# Or if already cloned, initialize submodules
git submodule update --init --recursive
```

## 🎓 Examples

### AI Second Brain

A persistent "second brain" for Claude to store and retrieve personal notes:

```bash
cd examples/ai-second-brain
dotnet run
```

Features:
- Store notes and learnings
- Semantic search for relevant context
- User persona management
- Automatic entity extraction

### Project Context Memory

Per-project memory isolation for development tools:

```bash
cd examples/project-context-memory
dotnet run
```

Features:
- Scoped memory per project/repository
- Metadata-based filtering
- Cross-project search capabilities
- Multi-tenant patterns

See [Examples README](./examples/README.md) for more details.

## 🔐 Security

SerialMemory includes production-ready security features:

### Multi-Tenancy
- Complete data isolation with PostgreSQL row-level security (RLS)
- Tenant-scoped API keys
- Secure tenant context propagation

### Authentication & Authorization
- JWT token-based API authentication
- Role-based access control (admin, user)
- Service-to-service authentication

### Data Protection
- All passwords and secrets in environment variables
- No default credentials in code
- CORS and CSRF protection
- SQL injection protection via parameterized queries

### Production Hardening
Use `docker-compose.prod.yml` for security-hardened deployment:

```bash
# Configure environment
cp .env.production.example .env
# Edit .env with strong passwords

# Start with production configuration
docker compose -f docker-compose.prod.yml up -d
```

See [Self-Hosting Guide](./docs/07-self-hosting.md) for detailed security setup.

## 🛠️ Technology Stack

- **Runtime**: .NET 10+
- **Database**: PostgreSQL 15+ with pgvector
- **Search**: Vector embeddings (384-dim)
- **Caching**: Redis
- **Queue**: RabbitMQ
- **NLP**: Pattern-based entity extraction (extensible to spaCy, BERT)
- **Dashboard**: React + TypeScript + Vite
- **Protocol**: Model Context Protocol (MCP)

## 📊 Performance

- Supports millions of memories per tenant
- Sub-100ms semantic search queries (with appropriate indexing)
- Horizontal scaling via PostgreSQL connection pooling
- Async processing queue for heavy operations

## 🧪 Testing

Run the test suite:

```bash
# Unit tests
dotnet test SerialMemory.Tests

# MCP integration tests
dotnet test SerialMemory.Mcp.Tests

# All tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## 📝 Development

### Setup Development Environment

```bash
# Install dependencies
dotnet restore

# Build solution
dotnet build

# Run with local infrastructure
docker compose up -d
dotnet run --project SerialMemory.Mcp
```

### Code Organization

- **SerialMemory.Core**: Domain models and business logic
- **SerialMemory.Infrastructure**: EF Core data access patterns
- **SerialMemory.Mcp**: MCP server implementation
- **SerialMemory.Api**: REST API endpoints
- **SerialMemory.Tests**: Unit and integration tests

## 🤝 Contributing

We welcome contributions! Please follow these steps:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow C# naming conventions (PascalCase for public members)
- Add tests for new features
- Update documentation for API changes
- Ensure all tests pass before submitting PR

## 📄 License

SerialMemory is released under the **MIT License**. See [LICENSE](./LICENSE) for details.

## 🆘 Support & Community

- **GitHub Issues**: [Report bugs and request features](https://github.com/sblanchard/SerialMemoryServer/issues)
- **Documentation**: [Full docs](./docs/)
- **Examples**: [Ready-to-run examples](./examples/)

## 🎯 Roadmap

- [ ] Python MCP server
- [ ] GraphQL API
- [ ] Advanced reasoning with multi-hop traversal
- [ ] Temporal graph visualization
- [ ] Plug-in system for custom entity types
- [ ] Multi-language support (embeddings & entity extraction)

## 🙏 Acknowledgments

SerialMemory is built on these excellent open-source projects:

- [PostgreSQL](https://www.postgresql.org) & [pgvector](https://github.com/pgvector/pgvector)
- [Ollama](https://ollama.ai) for local embeddings
- [Model Context Protocol](https://modelcontextprotocol.io) by Anthropic

---

**Made with ❤️ for AI-powered applications**
