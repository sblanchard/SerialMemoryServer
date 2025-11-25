# SerialMemoryServer - CORE-like Knowledge Graph Memory System

A **temporal knowledge graph memory system** implementing the Model Context Protocol (MCP), inspired by [CORE (getcore.me)](https://getcore.me/).

> **Purpose**: Build your personal memory system to power your AI apps. Like CORE, but open-source, self-hosted, and built with PostgreSQL + pgvector for semantic search, entity extraction, relationship tracking, and multi-hop reasoning.

## ✨ Key Features

🧠 **Temporal Knowledge Graph** - Track who said what, when, and why with full provenance
🔍 **Semantic Search** - Find memories by meaning using sentence-transformers embeddings
🕸️ **Relationship Extraction** - Automatically extract entities and relationships using spaCy
🎯 **Multi-Hop Reasoning** - Traverse knowledge graph connections for complex queries
👤 **User Personas** - Learn and recall user preferences, skills, and background
📊 **Conversation Sessions** - Track context across interactions
⚡ **Local-First** - No API keys required, runs entirely offline with local ML models

## 🚀 Quick Start

### Option 1: Local Development (Recommended for AI Integration)

1. **Start infrastructure services:**
```bash
docker compose up -d postgres redis rabbitmq
```

2. **Install Python dependencies:**
```bash
cd SerialMemory.Mcp.Python
pip install -r requirements.txt
python -m spacy download en_core_web_sm
```

3. **Run the MCP server:**
```bash
python -m src.main
```

### Option 2: Full Docker Stack (Demo/Testing)

```bash
# Start everything including .NET API/Worker
docker compose up --build
```

## 📊 Services

Once running, access:

| Service | URL | Credentials |
|---------|-----|-------------|
| **API (Swagger)** | http://localhost:5000/swagger | - |
| **API Metrics** | http://localhost:5000/metrics | - |
| **Worker Metrics** | http://localhost:8081/metrics | - |
| **RabbitMQ Management** | http://localhost:15672 | guest/guest |
| **Prometheus** | http://localhost:9090 | - |
| **Grafana** | http://localhost:3000 | admin/admin |
| **Redis** | localhost:6379 | - |
| **PostgreSQL** | localhost:5432 | postgres/postgres |

## 🏗️ Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                  MCP Server (Python/STDIO)                   │
│         ┌────────────────────────────────────┐               │
│         │  sentence-transformers (embeddings)│               │
│         │  spaCy (entity extraction)         │               │
│         │  Knowledge Graph Service           │               │
│         └────────────────────────────────────┘               │
│                         ↓                                     │
│         ┌────────────────────────────────────┐               │
│         │  PostgreSQL + pgvector             │               │
│         │  - memories (with embeddings)      │               │
│         │  - entities & relationships        │               │
│         │  - user personas                   │               │
│         │  - conversation sessions           │               │
│         └────────────────────────────────────┘               │
└──────────────────────────────────────────────────────────────┘
```

### Data Flow

**Memory Ingestion:**
1. Client sends memory via `memory_ingest` tool
2. Generate embedding using sentence-transformers
3. Extract entities and relationships using spaCy
4. Store memory, entities, and relationships in PostgreSQL
5. Link entities to memory via many-to-many relationship

**Semantic Search:**
1. Client queries via `memory_search` tool
2. Generate query embedding
3. Search memories using pgvector cosine similarity
4. Optionally combine with full-text search (hybrid mode)
5. Enrich results with linked entities and relationships

**Multi-Hop Reasoning:**
1. Find initial memories matching query
2. Extract entities from those memories
3. Traverse entity relationships
4. Find connected memories via related entities
5. Return graph of memories, entities, and relationships

### Components

- **Python MCP Server** - Temporal knowledge graph with semantic search
- **PostgreSQL + pgvector** - Graph storage with vector similarity search
- **sentence-transformers** - Local embedding generation (384-dim vectors)
- **spaCy** - Entity extraction and relationship detection
- **Redis** (optional) - Caching layer for hot memories
- **.NET API/Worker** (optional) - HTTP REST API and real-time SignalR updates

## 🔧 Development

```bash
# Build solution
dotnet build SerialMemoryServer.sln

# Run locally (requires Redis + RabbitMQ running)
dotnet run --project SerialMemory.Api
dotnet run --project SerialMemory.Worker
dotnet run --project SerialMemory.Mcp
```

## 🤖 MCP Integration

Add to your Claude Desktop `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "serial-memory": {
      "command": "python",
      "args": ["-m", "src.main"],
      "cwd": "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp.Python",
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb"
      }
    }
  }
}
```

### Available MCP Tools

| Tool | Description |
|------|-------------|
| **memory_search** | Search memories using semantic/text/hybrid search with entity enrichment |
| **memory_ingest** | Add new memories with automatic entity/relationship extraction |
| **memory_about_user** | Retrieve user persona (preferences, skills, background) |
| **initialise_conversation_session** | Start a new conversation session for context tracking |
| **end_conversation_session** | End the current conversation session |
| **memory_multi_hop_search** | Traverse knowledge graph for multi-hop reasoning |
| **get_integrations** | List available external integrations |

### Available MCP Resources

| Resource | Description |
|----------|-------------|
| **memory://recent** | List of recently added memories (JSON) |
| **memory://sessions** | List of recent conversation sessions (JSON) |

### Example Usage

```python
# Add a memory
memory_ingest({
  "content": "I met John Smith at the conference. He works at Acme Corp on AI research.",
  "source": "claude-desktop"
})
# → Automatically extracts entities: John Smith (PERSON), Acme Corp (ORG)
# → Extracts relationship: John Smith WORKS_AT Acme Corp

# Search semantically
memory_search({
  "query": "Who did I meet at the conference?",
  "mode": "hybrid",
  "limit": 5
})
# → Returns relevant memories with entities and similarity scores

# Multi-hop reasoning
memory_multi_hop_search({
  "query": "AI research",
  "hops": 2
})
# → Finds memories about AI research
# → Follows entity relationships to find connected memories
```

## 📡 API Examples

```bash
# List all contexts
curl http://localhost:5000/context

# Get specific context
curl http://localhost:5000/context/mykey

# Set context
curl -X POST http://localhost:5000/context/mykey -d "my value"

# Delete context
curl -X DELETE http://localhost:5000/context/mykey

# View metrics
curl http://localhost:5000/metrics
curl http://localhost:8081/metrics
```

## 📈 Observability

**Custom Metrics:**
- `rabbit_published_total` - Events published to RabbitMQ
- `rabbit_consumed_total` - Events consumed by Worker
- `redis_latency_ms` - Redis operation latency histogram

**Built-in Metrics:**
- Runtime instrumentation (GC, memory, threads)
- Process instrumentation (CPU, handles)
- HTTP instrumentation (request duration, status codes)

## 🛠️ Technology Stack

### Python MCP Server (Primary)
- **Python 3.11+**
- **MCP SDK** - Model Context Protocol implementation
- **sentence-transformers** - Semantic embeddings (all-MiniLM-L6-v2)
- **spaCy** - NLP and entity extraction (en_core_web_sm)
- **pgvector** - Vector similarity search
- **psycopg** - Async PostgreSQL driver
- **PyTorch** - ML framework backend

### Infrastructure
- **PostgreSQL 17 + pgvector** - Knowledge graph storage
- **Redis 7** - Optional caching layer
- **RabbitMQ 3** - Optional event streaming (for .NET API)

### .NET Services (Optional)
- **.NET 9** with C# 13
- **SignalR** - Real-time WebSocket updates
- **OpenTelemetry** - Metrics & tracing
- **Prometheus + Grafana** - Observability

## 🎯 Technical Highlights

This project demonstrates:

✅ **Temporal Knowledge Graphs** - Entity-relationship models with provenance tracking
✅ **Semantic Search** - Vector embeddings + pgvector for similarity search
✅ **NLP Pipeline** - Entity extraction, relationship detection, dependency parsing
✅ **Model Context Protocol** - STDIO server for AI agent integration
✅ **Multi-Hop Reasoning** - Graph traversal for complex queries
✅ **Local-First ML** - No external API dependencies
✅ **Clean Architecture** - Layered design (DB → Services → Tools → Server)
✅ **Async/Await** - Fully async Python with connection pooling
✅ **Containerization** - Docker Compose orchestration

## 📊 Database Schema

The knowledge graph uses 8 core tables:

- **memories** - Core episodes with embeddings (vector(384))
- **entities** - Extracted entities (PERSON, ORG, GPE, DATE, etc.)
- **entity_relationships** - Relationships between entities
- **memory_entities** - Many-to-many memory↔entity links
- **user_personas** - User preferences, skills, background
- **conversation_sessions** - Session tracking
- **integrations** + **integration_actions** - External tool registry

Indexes: pgvector IVFFlat, full-text search (tsvector), foreign keys

## 🔬 Comparison with CORE

| Feature | CORE (getcore.me) | SerialMemoryServer |
|---------|-------------------|-------------------|
| Temporal knowledge graph | ✅ | ✅ |
| Semantic search | ✅ | ✅ (pgvector) |
| Entity extraction | ✅ | ✅ (spaCy) |
| Relationship tracking | ✅ | ✅ |
| Multi-hop reasoning | ✅ | ✅ |
| User personas | ✅ | ✅ |
| MCP integration | ✅ | ✅ |
| Open source | ❌ | ✅ |
| Self-hosted | Paid | ✅ Free |
| Local-first (no APIs) | ❌ | ✅ |
| LoCoMo benchmark | 88.24% | Not tested |

## 📝 Notes

- Multi-hop reasoning is partially implemented (traversal logic can be enhanced)
- User persona extraction is rule-based (could be improved with LLM)
- No authentication/authorization (demo purposes)
- Redis integration is optional (can be removed if not needed)

## 📄 License

MIT
