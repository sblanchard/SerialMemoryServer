# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SerialMemoryServer is a temporal knowledge graph memory system. It provides:

- Temporal knowledge graph with semantic search
- Entity extraction and relationship tracking
- Semantic embeddings (384-dim vectors)
- PostgreSQL + pgvector for storage
- MCP protocol for AI agent integration

## Available Implementations

### 1. C# MCP Server (`SerialMemory.Mcp/`) - **RECOMMENDED**

Full-featured MCP server in C# with:
- Complete knowledge graph functionality
- Pattern-based entity extraction
- HTTP embedding service integration
- CORE import tool for migration
- PostgreSQL + pgvector storage

### 2. Python MCP Server (`SerialMemory.Mcp.Python/`)

Alternative implementation with:
- spaCy for NLP/entity extraction
- sentence-transformers for embeddings
- Same PostgreSQL backend

### 3. .NET Services (Optional)

Supporting services:
- `SerialMemory.Api` - REST API with SignalR
- `SerialMemory.Worker` - RabbitMQ consumer
- `SerialMemory.Core` & `SerialMemory.Infrastructure` - Domain/infra layers
- `SerialMemory.ML` - Embedding and entity extraction services

## Build & Development Commands

### C# MCP Server (Recommended)

```bash
# Restore packages
dotnet restore

# Build the MCP server
dotnet build SerialMemory.Mcp/SerialMemory.Mcp.csproj

# Run the MCP server
dotnet run --project SerialMemory.Mcp

# Publish self-contained executable
dotnet publish SerialMemory.Mcp -c Release -r win-x64 --self-contained
```

### Python MCP Server (Alternative)

```bash
cd SerialMemory.Mcp.Python
pip install -r requirements.txt
python -m spacy download en_core_web_sm
python -m src.main
```

### Full Solution Build

```bash
dotnet restore
dotnet build SerialMemoryServer.sln
```

## Docker Compose

### Infrastructure Only (Recommended for Development)

```bash
# Start PostgreSQL (with pgvector), Redis, RabbitMQ
docker compose up -d postgres redis rabbitmq

# Run MCP server locally
dotnet run --project SerialMemory.Mcp

# Stop infrastructure
docker compose down
```

### Full Stack

```bash
docker compose up --build
docker compose down -v  # Stop and remove volumes
```

**Services Available:**
- **PostgreSQL**: localhost:5435 (postgres/postgres, db: contextdb)
- **Redis**: localhost:6379
- **RabbitMQ Management**: http://localhost:15672 (guest/guest)

## VPS Production Deployment (Security Hardened)

For production deployment on a VPS, use `docker-compose.prod.yml` which includes:

### Security Features

- **No default credentials** - All services require strong passwords from environment variables
- **Separate database users** - Admin user for setup, limited-privilege app user for runtime
- **Network isolation** - Internal services (PostgreSQL, Redis, RabbitMQ) not exposed to host
- **Redis authentication** - Password-protected Redis
- **RabbitMQ hardened** - Custom credentials, dedicated vhost, no guest user
- **Grafana hardened** - No anonymous access, secure cookies

### Quick Start

```bash
# 1. Copy and configure environment
cp .env.production.example .env
# Edit .env and fill in ALL required values

# 2. Generate secure passwords
openssl rand -base64 32  # For each password field
openssl rand -base64 64  # For JWT_SECRET and INTERNAL_TOKEN_KEY

# 3. Start the stack
docker compose -f docker-compose.prod.yml up -d

# 4. Check logs
docker compose -f docker-compose.prod.yml logs -f
```

### Required Environment Variables

| Variable | Description |
|----------|-------------|
| `POSTGRES_ADMIN_PASSWORD` | PostgreSQL admin password (setup only) |
| `POSTGRES_USER` | Application database user |
| `POSTGRES_PASSWORD` | Application database password |
| `REDIS_PASSWORD` | Redis authentication password |
| `RABBITMQ_USER` | RabbitMQ username |
| `RABBITMQ_PASSWORD` | RabbitMQ password |
| `JWT_SECRET` | JWT signing secret (64+ chars) |
| `INTERNAL_TOKEN_KEY` | Service-to-service token key |
| `SERVICE_API_KEY` | Admin API key |
| `GRAFANA_PASSWORD` | Grafana admin password |
| `STRIPE_*` | Stripe keys (if using SaaS mode) |

### Network Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    External Network                          │
│  ┌─────────┐  ┌───────────────┐  ┌─────────────┐           │
│  │   API   │  │ Dashboard API │  │  Web Admin  │           │
│  │  :5000  │  │    :5001      │  │   :5002     │           │
│  └────┬────┘  └──────┬────────┘  └──────┬──────┘           │
└───────┼──────────────┼──────────────────┼──────────────────┘
        │              │                  │
┌───────┴──────────────┴──────────────────┴──────────────────┐
│                    Internal Network                          │
│  ┌──────────┐  ┌───────┐  ┌──────────┐  ┌─────────────┐   │
│  │ Postgres │  │ Redis │  │ RabbitMQ │  │   Worker    │   │
│  │  (5432)  │  │(6379) │  │  (5672)  │  │   (8081)    │   │
│  └──────────┘  └───────┘  └──────────┘  └─────────────┘   │
│  ┌────────────┐  ┌─────────┐                               │
│  │ Prometheus │  │ Grafana │  (monitoring - internal only) │
│  │   (9090)   │  │ (3000)  │                               │
│  └────────────┘  └─────────┘                               │
└────────────────────────────────────────────────────────────┘
```

### Firewall Configuration

Only expose necessary ports:

```bash
# UFW example
ufw allow 22/tcp    # SSH
ufw allow 80/tcp    # HTTP (for Let's Encrypt)
ufw allow 443/tcp   # HTTPS
ufw allow 5000/tcp  # API (or use reverse proxy)
ufw allow 5002/tcp  # Web Admin (or use reverse proxy)
ufw enable
```

### Recommended: Reverse Proxy with Cloudflare Tunnel

For secure external access without exposing ports:

```bash
# Install cloudflared
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 -o cloudflared
chmod +x cloudflared
sudo mv cloudflared /usr/local/bin/

# Login and create tunnel
cloudflared tunnel login
cloudflared tunnel create serialmemory

# Configure (ops/cloudflared/config.yml)
# Point to localhost:5000 (API), localhost:5002 (Web)
cloudflared tunnel run serialmemory
```

### Database Backup

```bash
# Backup
docker exec serialmemory-postgres pg_dump -U pgadmin contextdb > backup.sql

# Restore
cat backup.sql | docker exec -i serialmemory-postgres psql -U pgadmin contextdb
```

## Architecture Overview

### C# MCP Server Architecture

```
SerialMemory.Mcp/
├── Program.cs                    # MCP server entry point & STDIO handler

SerialMemory.Core/
├── Models/
│   ├── Memory.cs                 # Memory/episode with embedding
│   ├── Entity.cs                 # Named entity (PERSON, ORG, etc.)
│   ├── EntityRelationship.cs     # Knowledge graph edges
│   ├── ConversationSession.cs    # Session tracking
│   └── UserPersona.cs            # User preferences/skills
├── Interfaces/
│   ├── IKnowledgeGraphStore.cs   # Data access contract
│   ├── IEmbeddingService.cs      # Embedding generation
│   └── IEntityExtractionService.cs # NER contract
└── Services/
    └── KnowledgeGraphService.cs  # Orchestration layer

SerialMemory.Infrastructure/
└── PostgresKnowledgeGraphStore.cs # PostgreSQL + pgvector implementation

SerialMemory.ML/
├── HttpEmbeddingService.cs       # HTTP API for embeddings
└── PatternEntityExtractionService.cs # Regex-based NER
```

### Database Schema

Located in `ops/init.sql`, the knowledge graph uses 8 tables:

- **memories** - Episodes with embeddings (vector(384)), content, timestamps
- **entities** - Named entities with types (PERSON, ORG, GPE, DATE, etc.)
- **entity_relationships** - Directed edges between entities
- **memory_entities** - Many-to-many links between memories and entities
- **user_personas** - User preferences, skills, background
- **conversation_sessions** - Session tracking
- **integrations** + **integration_actions** - External tool registry

## MCP Server Configuration

### Environment Variables

**Database:**
- `POSTGRES_HOST` - PostgreSQL host (default: localhost)
- `POSTGRES_PORT` - PostgreSQL port (default: 5432)
- `POSTGRES_USER` - Database user (default: postgres)
- `POSTGRES_PASSWORD` - Database password (default: postgres)
- `POSTGRES_DB` - Database name (default: contextdb)

**Embeddings - Ollama (recommended):**
- `OLLAMA_URL` - Local Ollama URL (default: http://localhost:11434)
- `OLLAMA_MODEL` - Embedding model (default: nomic-embed-text)
- `OLLAMA_EMBEDDING_DIM` - Embedding dimension (default: 768)
- `OLLAMA_CLOUD_API_KEY` - Set this to use Ollama Cloud instead of local Ollama

**Embeddings - Legacy Options:**
- `ONNX_MODEL_PATH` - Path to ONNX model file (pure C#, no Python)
- `VOCAB_PATH` - Path to vocab.txt for ONNX model
- `EMBEDDING_SERVICE_URL` - HTTP embedding service URL (default: http://localhost:8765)

### MCP Tools Available

| Tool | Description |
|------|-------------|
| `memory_search` | Search memories using semantic/text/hybrid search |
| `memory_ingest` | Add memories with automatic entity/relationship extraction |
| `memory_about_user` | Retrieve user persona (preferences, skills, background) |
| `set_user_persona` | Set/update user persona attributes |
| `initialise_conversation_session` | Start a new conversation session |
| `end_conversation_session` | End the current conversation session |
| `memory_multi_hop_search` | Traverse knowledge graph for multi-hop reasoning |
| `get_integrations` | List available external integrations |
| `import_from_core` | **Import from CORE MCP** (entities, relations, observations) |
| `crawl_relationships` | Extract entities/relationships from existing memories |
| `get_graph_statistics` | Get knowledge graph statistics (counts by type) |
| `get_model_info` | Get current embedding model info and upgrade options |
| `reembed_memories` | Re-generate embeddings after switching models |

### MCP Resources Available

- `memory://recent` - List of recently added memories (JSON)
- `memory://sessions` - List of recent conversation sessions (JSON)

### Claude Desktop Configuration (C#)

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "serial-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp"],
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb",
        "EMBEDDING_SERVICE_URL": "http://localhost:8765"
      }
    }
  }
}
```

Or use a published executable:

```json
{
  "mcpServers": {
    "serial-memory": {
      "command": "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp\\bin\\Release\\net9.0\\win-x64\\publish\\SerialMemory.Mcp.exe",
      "args": [],
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

## CORE Import Feature

The `import_from_core` tool allows you to migrate your data from CORE (getcore.me):

### Export from CORE

Use CORE's export functionality to get your data as JSON.

### Import Format

```json
{
  "entities": [
    {
      "name": "John Smith",
      "entityType": "PERSON",
      "observations": [
        "Works as a software engineer",
        "Lives in San Francisco",
        "Expert in Python and C#"
      ]
    },
    {
      "name": "Acme Corp",
      "entityType": "ORG",
      "observations": [
        "Technology company founded in 2010",
        "Headquartered in Silicon Valley"
      ]
    }
  ],
  "relations": [
    {
      "from": "John Smith",
      "to": "Acme Corp",
      "relationType": "works at"
    }
  ]
}
```

### Import via MCP Tool

```
Use the import_from_core tool with your CORE export data
```

The import will:
1. Create entities in the knowledge graph
2. Store observations as linked memories
3. Create relationship edges between entities
4. Generate embeddings for semantic search

## Embedding Models

The system supports multiple ONNX embedding models with automatic dimension detection and optimal pooling strategies.

### Supported Models

#### Small Models (384 dimensions) - Fast, good for most use cases
| Model | Description | Pooling |
|-------|-------------|---------|
| **all-MiniLM-L6-v2** | Default, best speed/quality balance | mean |
| **all-MiniLM-L12-v2** | Better quality, 2x slower | mean |
| **bge-small-en-v1.5** | Optimized for retrieval | cls |
| **e5-small-v2** | Good for asymmetric search | mean |

#### Medium Models (768 dimensions) - Better quality, recommended upgrade
| Model | Description | Pooling |
|-------|-------------|---------|
| **all-mpnet-base-v2** | **RECOMMENDED** - Best quality in class | mean |
| **bge-base-en-v1.5** | Excellent for retrieval | cls |
| **e5-base-v2** | Strong asymmetric search | mean |
| **gte-base** | Alibaba's high-quality encoder | mean |

#### Large Models (1024 dimensions) - Highest quality, significant resources
| Model | Description | Pooling |
|-------|-------------|---------|
| **e5-large-v2** | Top-tier quality | mean |
| **bge-large-en-v1.5** | Best retrieval model | cls |
| **gte-large** | Highest quality general encoder | mean |

### Option 1: ONNX (Pure C#, No Python Required) - RECOMMENDED

1. **Export the model to ONNX:**
```bash
pip install optimum[exporters] onnx
# Export your chosen model (e.g., all-mpnet-base-v2 for better quality)
optimum-cli export onnx --model sentence-transformers/all-mpnet-base-v2 ./models/all-mpnet-base-v2/
```

2. **Configure environment variables:**
```json
{
  "env": {
    "ONNX_MODEL_PATH": "D:\\models\\all-mpnet-base-v2\\model.onnx",
    "VOCAB_PATH": "D:\\models\\all-mpnet-base-v2\\vocab.txt"
  }
}
```

The system auto-detects the model type and applies correct pooling strategy.

### Switching to a Better Model

1. **Export the new ONNX model** (see above)

2. **Migrate database dimension** (if changing from 384):
```bash
# Edit ops/migrate_embedding_dimension.sql and set EMBEDDING_DIM to 768
psql -d contextdb -f ops/migrate_embedding_dimension.sql
```

3. **Update environment and restart** MCP server

4. **Re-embed all memories** using the `reembed_memories` MCP tool:
```
Use reembed_memories with force_all: true
```

### Option 2: HTTP Embedding Service (Requires Python)

Start the Python embedding service:

```bash
cd SerialMemory.Mcp.Python
python tools/embedding_http_service.py
```

Or create a simple FastAPI service:

```python
from fastapi import FastAPI
from sentence_transformers import SentenceTransformer
import uvicorn

app = FastAPI()
model = SentenceTransformer('sentence-transformers/all-mpnet-base-v2')  # Or any model

@app.post("/embed")
def embed(request: dict):
    return {"embedding": model.encode(request["text"]).tolist()}

@app.post("/embed-batch")
def embed_batch(request: dict):
    return {"embeddings": [e.tolist() for e in model.encode(request["texts"])]}

if __name__ == "__main__":
    uvicorn.run(app, port=8765)
```

Configure with:
```json
{
  "env": {
    "EMBEDDING_SERVICE_URL": "http://localhost:8765"
  }
}
```

## Data Flow

### Memory Ingestion Flow
1. AI agent calls `memory_ingest` tool via MCP
2. C# server calls HTTP embedding service (384-dim vector)
3. Pattern-based entity extraction identifies entities/relationships
4. Stores memory in PostgreSQL with embedding
5. Creates/updates entities and relationships
6. Links entities to memory via junction table

### Semantic Search Flow
1. AI agent calls `memory_search` with natural language query
2. Server generates query embedding via HTTP service
3. PostgreSQL performs vector similarity search (pgvector)
4. Optionally combines with full-text search (hybrid mode)
5. Enriches results with linked entities
6. Returns ranked memories with similarity scores

### Multi-Hop Reasoning Flow
1. AI agent calls `memory_multi_hop_search`
2. Server finds initial memories matching query
3. Extracts entities from results
4. Traverses entity_relationships to find connected entities
5. Finds memories linked to related entities
6. Returns graph structure with memories, entities, relationships

## Technology Stack

- **.NET 9.0** with C# 12
- **PostgreSQL** with pgvector extension
- **Npgsql** + **Dapper** for data access
- **sentence-transformers** (via HTTP) for embeddings
- **Pattern-based NER** for entity extraction
- **MCP Protocol** over STDIO

## Event-Sourced Memory Engine (`SerialMemory.EventSourcing/`)

A fully event-sourced cognitive memory platform with layered memory, confidence decay, multi-axis retrieval, and autonomous self-healing.

### Event Types

All memory mutations are append-only events (never modified or deleted):

| Event | Description |
|-------|-------------|
| `MemoryCreated` | New memory added to the system |
| `MemoryUpdated` | Memory content was modified |
| `MemoryMerged` | Two or more memories combined |
| `MemoryInvalidated` | Memory soft-deleted (superseded/contradicted) |
| `MemoryDecayed` | Confidence decreased due to time |
| `MemoryReinforced` | Memory validated, decay reset |
| `MemoryLayerTransitioned` | Memory moved between layers |
| `MemoryArchived` | Memory moved to cold storage |
| `MemoryRecalled` | Memory was accessed during retrieval |
| `MemoryIgnored` | Memory was present but skipped |
| `MemoryContradicted` | Contradiction detected with another memory |
| `MemoryExpired` | TTL policy triggered expiration |
| `MemorySplit` | Memory decomposed into children |

### Memory Layers

Each memory belongs to exactly one cognitive layer:

| Layer | Description |
|-------|-------------|
| `L0_RAW` | Raw transcript or input data |
| `L1_CONTEXT` | Contextual understanding of raw input |
| `L2_SUMMARY` | Summarized information |
| `L3_KNOWLEDGE` | Extracted knowledge and facts |
| `L4_HEURISTIC` | Heuristics and learned patterns |

### Confidence & Decay

Each memory stores:
- `confidenceScore` (0.0–1.0) - Current confidence level
- `halfLifeDays` (integer) - Decay rate
- `lastReinforcedAt` (UTC timestamp) - Last validation time

Decay is calculated as: `confidence * 0.5^(daysSinceReinforcement / halfLifeDays)`

### Multi-Axis Retrieval Engine

Retrieval scoring combines multiple factors with weighted aggregation:

| Score Component | Description | Default Weight |
|-----------------|-------------|----------------|
| `semanticScore` | Vector similarity (pgvector) | 0.35 |
| `recencyScore` | Age-based decay | 0.15 |
| `confidenceScore` | Current confidence after decay | 0.20 |
| `userAffinityScore` | User-specific relevance | 0.15 |
| `directiveMatchScore` | Match to current goals | 0.15 |
| `contradictionPenalty` | Penalty per contradiction | -0.10 |

**Final Score** = Σ(score × weight) - (contradictions × penalty)

### Memory Integrity

Every memory includes:
- `contentHash` - SHA-256 hash verified on read
- `causalParents[]` - Links to parent memories
- `validatedBy[]` - Memories that validated this one

### CQRS Commands

Write commands (all produce events):

| Command | Description |
|---------|-------------|
| `CreateMemoryCommand` | Create new memory with embedding |
| `UpdateMemoryCommand` | Update memory content |
| `ReinforceMemoryCommand` | Reset decay, boost confidence |
| `InvalidateMemoryCommand` | Soft-delete (superseded/contradicted) |
| `MergeMemoriesCommand` | Combine multiple memories |
| `TransitionLayerCommand` | Move memory between layers |
| `ApplyDecayCommand` | Apply time-based decay |
| `ArchiveMemoryCommand` | Move to cold storage |
| `RecallMemoryCommand` | Record retrieval event |
| `MarkContradictionCommand` | Flag contradiction |
| `ExpireMemoryCommand` | Apply TTL expiration |
| `SplitMemoryCommand` | Decompose into children |

### CQRS Queries

Read queries (from projections):

| Query | Description |
|-------|-------------|
| `SearchMemoriesQuery` | Multi-axis semantic search |
| `GetMemoryByIdQuery` | Get by ID with integrity check |
| `GetRelatedMemoriesQuery` | Traverse causal graph |
| `FindDuplicatesQuery` | Find similar memories |
| `GetLayerStatisticsQuery` | Stats by memory layer |
| `GetRecentMemoriesQuery` | Recent memories list |
| `GetCognitiveStageLogsQuery` | Query cognitive stage logs |

### Streaming

**Redis Streams** (`RedisEventStreamPublisher`):
- Durable, ordered event delivery
- Consumer groups for scaling
- Stream key: `memory:events`
- Auto-creates `projections` and `maintenance` consumer groups

**WebSocket** (`WebSocketEventHub`):
- Real-time event broadcasting
- Subscription filtering by event type and stream ID
- Commands: `subscribe`, `ping`

### Autonomous Maintenance Workers

**MemoryMaintenanceWorker** (background service):
- Runs periodic maintenance cycles
- Apply decay to old memories
- Archive cold memories (low access, low confidence)
- Reinforce stable memories (frequently accessed)
- Detect potential duplicates
- Detect potential contradictions
- All mutations via commands (event sourcing preserved)

**MaintenanceTaskProcessor** (background service):
- Processes pending maintenance tasks
- Merge detected duplicates
- Resolve contradictions
- Uses `FOR UPDATE SKIP LOCKED` for concurrent processing

Configuration (`MaintenanceConfig`):
```csharp
CycleInterval = TimeSpan.FromHours(1)
ArchiveConfidenceThreshold = 0.1f
MinAccessCountForRetention = 3
ColdPeriodDays = 30
ReinforceMinConfidence = 0.7f
ReinforceMinAccessCount = 10
ReinforceIntervalDays = 7
DuplicateSimilarityThreshold = 0.95f
```

### Export System

**Full JSON Export**:
```csharp
var exporter = new MemoryExporter(connectionString, logger);
var result = await exporter.ExportFullJsonAsync("export.json", new ExportOptions
{
    IncludeEntities = true,
    IncludeRelationships = true,
    IncludeEvents = false,
    ActiveOnly = true,
    EncryptionKey = "optional-key",  // AES-256 encryption
    Compress = true                   // GZip compression
});
```

**Chunked Export** (resumable):
```csharp
await foreach (var chunk in exporter.ExportChunkedAsync(chunkSize: 1000, fromSequence: 0))
{
    // Process chunk.Memories
    // Resume from chunk.ToSequence if interrupted
}
```

**Events Export** (for replay):
```csharp
await exporter.ExportEventsAsync("events.json", fromSequence: 0);
```

### Event Store

**PostgresEventStore** features:
- Append-only semantics
- Optimistic concurrency control
- Global sequence for ordering
- Stream subscription for projections
- Content hash verification

## Claude Code Hooks Integration

For automatic memory integration, add to your `.claude/settings.json`:

```json
{
  "hooks": {
    "SessionStart": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "echo 'Memory System Active. Use mcp__serial-memory__memory_search for context.'"
      }]
    }],
    "UserPromptSubmit": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "echo 'CONTEXT: Use mcp__serial-memory__memory_search to find relevant context.'"
      }]
    }],
    "PreCompact": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "echo 'WARNING: Context compacting! Save critical context with mcp__serial-memory__memory_ingest NOW.'"
      }]
    }],
    "Stop": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "echo 'Consider: mcp__serial-memory__memory_ingest for important insights.'"
      }]
    }],
    "SessionEnd": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "echo 'Session ending. Save summary with mcp__serial-memory__memory_ingest.'"
      }]
    }]
  }
}
```

**Available Hook Events:**
- `SessionStart` - Session begins
- `UserPromptSubmit` - User submits prompt
- `PreToolUse` - Before tool executes
- `PostToolUse` - After tool completes
- `PermissionRequest` - Permission dialog shown
- `Notification` - Notifications triggered
- `Stop` - Main agent finishes
- `SubagentStop` - Subagent finishes
- `PreCompact` - Before context compaction (critical!)
- `SessionEnd` - Session terminates

## Current Implementation Status

**Core MCP Server:**
- ✅ Full C# MCP Server with knowledge graph tools
- ✅ KnowledgeGraphService orchestration layer
- ✅ PostgresKnowledgeGraphStore (complete CRUD)
- ✅ Pattern-based entity extraction
- ✅ HTTP/ONNX embedding service integration
- ✅ CORE import functionality
- ✅ Multi-hop graph traversal
- ✅ User persona management
- ✅ Conversation session tracking

**Event-Sourced Engine:**
- ✅ Full event sourcing with 13 event types
- ✅ 5-layer memory hierarchy (L0-L4)
- ✅ Confidence decay with half-life
- ✅ Multi-axis retrieval (6 scoring factors)
- ✅ SHA-256 content integrity verification
- ✅ CQRS command/query separation
- ✅ PostgreSQL event store with global sequence
- ✅ Redis Streams for durable messaging
- ✅ WebSocket real-time broadcasting
- ✅ Autonomous maintenance workers
- ✅ Duplicate detection & merging
- ✅ Contradiction detection
- ✅ Full/chunked/encrypted exports

**Architecture:**
- ✅ Clean Architecture (Core → Infrastructure → Mcp)
- ✅ Event sourcing (append-only, immutable)
- ✅ CQRS (separate read/write paths)
- ✅ Async/await throughout
- ✅ Connection pooling
- ✅ Optimistic concurrency control
- ✅ Restart-safe background workers

## Memory Protocol

This project uses SerialMemory MCP for persistent context across sessions.

### Session Start
- **ALWAYS** use the `memory-search` agent before answering substantive questions
- Search for: project name, current task keywords, recent decisions
- Query examples: "FlexPilot waterfall rendering", "IC-7610 CAT emulation", "BGRA pixel format"

### During Work
- Search memory when encountering:
  - References to "we discussed", "last time", "previous session"
  - Technical decisions that may have prior context
  - Recurring patterns or known issues
  - Architecture questions about existing components

### Session End / After Significant Work
- **ALWAYS** use the `memory-ingest` agent to store:
  - Decisions made and their rationale
  - Bugs fixed with root cause analysis
  - Architecture patterns discovered or implemented
  - User preferences learned
  - Implementation details that took effort to figure out

### Memory Content Format
```
[Project] Session Summary - YYYY-MM-DD
Category: decision | bugfix | architecture | learning
Topic: [Brief description]
---
1. [ISSUE/DECISION NAME]
Problem: [What was wrong or needed]
Root Cause: [Why it happened]
Solution: [What fixed it / what was decided]
Files: [Affected files]
---
[Additional items...]
```

### What to Store
- Root cause analyses (like BGRA8888 channel swap)
- Exact formulas and magic numbers (threshold calculations, gradient stops)
- Architecture decisions with trade-offs
- Protocol specifications and quirks discovered
- Performance optimization findings

### What NOT to Store
- Raw code blocks (describe conceptually instead)
- Transient debugging output
- Obvious/trivial fixes
- Sensitive credentials or keys

### Memory Search Strategies
- **By project**: "FlexPilot", "FlexHPSDR", "RebateX"
- **By component**: "waterfall", "spectrum", "CAT emulation"
- **By problem type**: "rendering", "protocol", "performance"
- **By date**: Include date in queries for recent context
- forget self hosted, remove it