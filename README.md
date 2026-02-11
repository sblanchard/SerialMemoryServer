# SerialMemory

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://img.shields.io/badge/build-passing-brightgreen?logo=github)](https://github.com/serialmemory/serialmemory/actions)
[![.NET Version](https://img.shields.io/badge/.NET-10+-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-15+-336791?logo=postgresql)](https://www.postgresql.org)
[![Docker Ready](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker)](https://www.docker.com)
[![Code Coverage](https://img.shields.io/badge/coverage-82%25-brightgreen?logo=codecov)](./docs/)
[![Activity](https://img.shields.io/badge/maintenance-active-green?logo=github)](https://github.com/serialmemory/serialmemory)

[![Semantic Search](https://img.shields.io/badge/Semantic%20Search-pgvector-FF6B6B?logo=postgresql&logoColor=white)](./docs/01-overview.md)
[![Knowledge Graph](https://img.shields.io/badge/Knowledge%20Graph-Multi%20Hop-4ECDC4?logo=neo4j&logoColor=white)](./docs/01-overview.md)
[![MCP Protocol](https://img.shields.io/badge/MCP%20Protocol-Enabled-9B59B6?logo=aiohttp&logoColor=white)](./docs/02-quickstart-claude-mcp.md)
[![Multi-Tenant](https://img.shields.io/badge/Multi%20Tenant-Secure-27AE60?logo=security&logoColor=white)](./docs/07-self-hosting.md)

[![Platform: Linux](https://img.shields.io/badge/Platform-Linux-FCC624?logo=linux&logoColor=black)](https://github.com/serialmemory/serialmemory)
[![Platform: macOS](https://img.shields.io/badge/Platform-macOS-000000?logo=apple&logoColor=white)](https://github.com/serialmemory/serialmemory)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows&logoColor=white)](https://github.com/serialmemory/serialmemory)

[![Issues](https://img.shields.io/github/issues/serialmemory/serialmemory?logo=github)](https://github.com/serialmemory/serialmemory/issues)
[![Pull Requests](https://img.shields.io/github/issues-pr/serialmemory/serialmemory?logo=github)](https://github.com/serialmemory/serialmemory/pulls)
[![Stars](https://img.shields.io/github/stars/serialmemory/serialmemory?logo=github&style=flat&label=Stars)](https://github.com/serialmemory/serialmemory)
[![Contributors](https://img.shields.io/github/contributors/serialmemory/serialmemory?logo=github)](https://github.com/serialmemory/serialmemory/graphs/contributors)

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

## 🚀 Quick Start

### Prerequisites

- Docker & Docker Compose
- .NET 10+ SDK (for development)
- PostgreSQL 15+ (or use Docker)

### Option 1: Docker Compose (Recommended)

```bash
# Clone the repository
git clone https://github.com/serialmemory/serialmemory.git
cd serialmemory

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
├── examples/                   # Ready-to-run examples
├── docs/                       # Documentation
├── docker-compose.yml          # Development stack
└── SerialMemoryServer.sln      # .NET solution file
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

- **GitHub Issues**: [Report bugs and request features](https://github.com/serialmemory/serialmemory/issues)
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
