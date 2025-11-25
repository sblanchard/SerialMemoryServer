# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SerialMemoryServer is a **CORE-like temporal knowledge graph memory system** inspired by [getcore.me](https://getcore.me/). It consists of:

1. **Python MCP Server** (`SerialMemory.Mcp.Python/`) - **PRIMARY COMPONENT**
   - Temporal knowledge graph with semantic search
   - Entity extraction using spaCy
   - Semantic embeddings using sentence-transformers
   - PostgreSQL + pgvector for storage
   - MCP protocol for AI agent integration

2. **.NET Services** (Optional) - Legacy HTTP API and Worker
   - `SerialMemory.Api` - REST API with SignalR
   - `SerialMemory.Worker` - RabbitMQ consumer
   - `SerialMemory.Core` & `SerialMemory.Infrastructure` - Domain/infra layers
   - `SerialMemory.Mcp` - Old .NET MCP server (superseded by Python version)

## Build & Development Commands

### Python MCP Server (Primary)

```bash
# Navigate to Python project
cd SerialMemory.Mcp.Python

# Install dependencies
pip install -r requirements.txt
python -m spacy download en_core_web_sm

# Run MCP server
python -m src.main

# Run with environment variables
POSTGRES_HOST=localhost POSTGRES_PORT=5432 python -m src.main
```

### .NET Services (Optional)

```bash
# Restore packages
dotnet restore

# Build the entire solution
dotnet build SerialMemoryServer.sln

# Run the API
dotnet run --project SerialMemory.Api

# Run the Worker
dotnet run --project SerialMemory.Worker
```

## Docker Compose

### Recommended: Infrastructure Only (For Python MCP Development)

Start only infrastructure, run Python MCP server locally:

```bash
# Start PostgreSQL (with pgvector), Redis, RabbitMQ
docker compose up -d postgres redis rabbitmq

# Run Python MCP server locally
cd SerialMemory.Mcp.Python
python -m src.main

# Stop infrastructure when done
docker compose down
```

**Benefits:**
- Debug Python code with breakpoints
- Hot reload on code changes
- Full access to logs and stdout
- Faster iteration cycle

### Option 2: Full Stack Demo (All Services)

```bash
# Build and start all services
docker compose up --build

# Or run in detached mode
docker compose up -d

# View logs
docker compose logs -f
docker compose logs -f postgres

# Stop all services
docker compose down

# Stop and remove volumes
docker compose down -v
```

**Services Available:**
- **PostgreSQL**: localhost:5432 (postgres/postgres, db: contextdb)
- **Redis**: localhost:6379
- **RabbitMQ Management**: http://localhost:15672 (guest/guest)
- **Prometheus**: http://localhost:9090
- **Grafana**: http://localhost:3000 (admin/admin)
- **.NET API** (if built): http://localhost:5000/swagger
- **Worker Metrics** (if built): http://localhost:8081/metrics

## Architecture Overview

SerialMemoryServer is a **temporal knowledge graph memory system** implementing the Model Context Protocol (MCP), inspired by CORE (getcore.me).

**Purpose**: Build your personal memory system to power your AI apps. Provides semantic search, entity extraction, relationship tracking, and multi-hop reasoning over a PostgreSQL knowledge graph with pgvector.

### Core Architecture (Python MCP Server)

The Python MCP server (`SerialMemory.Mcp.Python/`) is organized in clean layers:

```
src/
├── main.py                          # MCP server entry point & STDIO handler
├── config.py                        # Configuration (Pydantic settings)
├── db/
│   └── postgres.py                 # PostgreSQL + pgvector data access
├── services/
│   ├── embedding_service.py        # sentence-transformers embeddings
│   ├── entity_extraction_service.py # spaCy NER & relationship extraction
│   └── knowledge_graph_service.py  # Orchestration layer
```

**Layer Responsibilities:**

1. **main.py** - MCP Protocol Handler
   - Implements MCP STDIO protocol
   - Registers MCP tools and resources
   - Handles tool invocation and JSON-RPC responses

2. **services/** - Business Logic
   - `EmbeddingService`: Generates 384-dim vectors using sentence-transformers
   - `EntityExtractionService`: Extracts entities (PERSON, ORG, GPE, etc.) and relationships using spaCy
   - `KnowledgeGraphService`: Orchestrates ingestion, search, and multi-hop reasoning

3. **db/postgres.py** - Data Access
   - `KnowledgeGraphDB`: Async PostgreSQL operations with pgvector
   - Connection pooling (min 2, max 10 connections)
   - Vector similarity search using cosine distance
   - Full-text search using tsvector
   - Entity and relationship CRUD operations

### Database Schema

Located in `ops/init.sql`, the knowledge graph uses 8 tables:

- **memories** - Episodes with embeddings (vector(384)), content, timestamps, provenance
- **entities** - Extracted entities with types (PERSON, ORG, GPE, DATE, etc.)
- **entity_relationships** - Directed edges between entities with confidence scores
- **memory_entities** - Many-to-many links between memories and entities
- **user_personas** - User preferences, skills, background attributes
- **conversation_sessions** - Session tracking for context continuity
- **integrations** + **integration_actions** - External tool/API registry

**Indexes:**
- pgvector IVFFlat index on embeddings for fast similarity search
- Full-text search (GIN) index on memory content
- B-tree indexes on foreign keys and timestamps

### Legacy .NET Services (Optional)

These services are maintained but optional:

1. **SerialMemory.Api** - ASP.NET Core Web API (Minimal APIs)
   - HTTP REST endpoints for context CRUD operations
   - SignalR hub (`/hub/context`) for real-time updates
   - Swagger documentation at `/swagger`
   - OpenTelemetry metrics exposed at `/metrics`

2. **SerialMemory.Worker** - Background worker service
   - Consumes RabbitMQ events
   - Exposes Prometheus metrics on port 8081

3. **SerialMemory.Core** & **SerialMemory.Infrastructure**
   - Domain layer and Redis/RabbitMQ implementations
   - Clean architecture pattern

4. **SerialMemory.Mcp** (C#)
   - Old .NET MCP server with basic key-value operations
   - **Superseded by Python version**

### Data Flow Architecture

**Memory Ingestion Flow:**
1. AI agent calls `memory_ingest` tool via MCP STDIO
2. Python server generates embedding using sentence-transformers (384-dim vector)
3. Extracts entities and relationships using spaCy NLP
4. Stores memory in PostgreSQL `memories` table with embedding
5. Creates entity records in `entities` table (or updates existing)
6. Creates relationship records in `entity_relationships` table
7. Links entities to memory via `memory_entities` junction table
8. Returns JSON-RPC response with created IDs and extracted data

**Semantic Search Flow:**
1. AI agent calls `memory_search` tool with natural language query
2. Server generates query embedding
3. PostgreSQL performs vector similarity search using pgvector (cosine distance)
4. Optionally combines with full-text search (hybrid mode)
5. Enriches results with linked entities from `memory_entities` join
6. Returns ranked memories with similarity scores and entity metadata

**Multi-Hop Reasoning Flow:**
1. AI agent calls `memory_multi_hop_search` with initial query
2. Server finds initial memories matching query (semantic + text search)
3. Extracts entities from returned memories
4. Traverses `entity_relationships` to find connected entities
5. Finds memories linked to related entities
6. Repeats for N hops
7. Returns graph structure with memories, entities, and relationships

### Key Design Patterns

- **Clean Architecture**: DB → Services → MCP Tools → Server
- **Async/Await**: Fully async Python with asyncio and async database drivers
- **Connection Pooling**: psycopg AsyncConnectionPool for concurrent queries
- **Vector Search**: pgvector IVFFlat index for sub-second similarity search
- **Dependency Injection**: Services initialized at startup, shared globally
- **Temporal Tracking**: All records have timestamps, provenance, and confidence scores

## MCP Server Configuration

The Python MCP server (`SerialMemory.Mcp.Python`) implements the Model Context Protocol for AI agent integration.

**Configuration** (via environment variables or `.env` file):
- `POSTGRES_HOST` - PostgreSQL host (default: localhost)
- `POSTGRES_PORT` - PostgreSQL port (default: 5432)
- `POSTGRES_USER` - Database user (default: postgres)
- `POSTGRES_PASSWORD` - Database password (default: postgres)
- `POSTGRES_DB` - Database name (default: contextdb)
- `EMBEDDING_MODEL` - sentence-transformers model (default: sentence-transformers/all-MiniLM-L6-v2)
- `SPACY_MODEL` - spaCy model (default: en_core_web_sm)
- `EMBEDDING_BATCH_SIZE` - Batch size for embeddings (default: 32)
- `MAX_SEARCH_RESULTS` - Max search results (default: 10)

**MCP Tools Available**:
- `memory_search` - Search memories using semantic/text/hybrid search
- `memory_ingest` - Add memories with automatic entity/relationship extraction
- `memory_about_user` - Retrieve user persona (preferences, skills, background)
- `initialise_conversation_session` - Start a new conversation session
- `end_conversation_session` - End the current conversation session
- `memory_multi_hop_search` - Traverse knowledge graph for multi-hop reasoning
- `get_integrations` - List available external integrations

**MCP Resources Available**:
- `memory://recent` - List of recently added memories (JSON)
- `memory://sessions` - List of recent conversation sessions (JSON)

**To use with Claude Desktop**:
Add to your `claude_desktop_config.json`:
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

**Note**: Update the `cwd` path to match your installation directory.

## API Endpoints

All endpoints are defined in `SerialMemory.Api/Program.cs`:

- `GET /context` - List all context keys
- `GET /context/{key}` - Retrieve specific context value
- `POST /context/{key}` - Create/update context (body is raw string, triggers SignalR broadcast)
- `DELETE /context/{key}` - Remove context (triggers SignalR broadcast)
- `GET /swagger` - API documentation
- `GET /metrics` - Prometheus metrics endpoint

**SignalR Hub**: `/hub/context`
- Methods: `Subscribe(key)`, `Unsubscribe(key)`
- Events: `ContextUpdated`, `ContextDeleted`

Write operations (POST/DELETE) publish events to RabbitMQ and broadcast to SignalR clients.

## Infrastructure Dependencies

### Redis
- Primary data store for context data
- Connection configured via `ConnectionStrings:Redis` in appsettings.json
- Uses `IConnectionMultiplexer` (StackExchange.Redis) as singleton

### RabbitMQ
- Event notification system using fanout exchange
- Default exchange: `"context.events"` (non-durable, fanout type)
- Host configured via `RabbitMq:Host` in appsettings.json
- `RabbitMqPublisher` is registered as singleton in API

### PostgreSQL
- Intended for Worker event persistence (not yet implemented)
- Will use Npgsql + Dapper for data access

## Configuration Requirements

The API expects `appsettings.json` with:

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "RabbitMq": {
    "Host": "localhost"
  }
}
```

## Current Implementation Status

**Completed:**
- ✅ **MCP STDIO Server** with full protocol implementation
- ✅ MCP Tools (`set_context`, `get_context`, `delete_context`, `list_contexts`)
- ✅ MCP Resources (`context://all`)
- ✅ Core domain models and interfaces
- ✅ Redis storage implementation (`RedisContextStore`)
- ✅ RabbitMQ event publishing (`RabbitMqPublisher`)
- ✅ RabbitMQ event consumption in Worker
- ✅ REST API with Swagger
- ✅ SignalR hub for real-time updates
- ✅ OpenTelemetry with Prometheus metrics (/metrics endpoints)
- ✅ Custom metrics (rabbit.published, rabbit.consumed)
- ✅ **Docker Compose orchestration** (full stack: API, Worker, Redis, RabbitMQ, PostgreSQL, Prometheus, Grafana)
- ✅ PostgreSQL database schema (context_snapshots table)

**Not Implemented:**
- ⏳ PostgreSQL persistence in Worker (schema ready, write logic TODO)
- ⏳ Configuration files (appsettings.json) - using environment variables in Docker
- ⏳ Authentication/authorization
- ⏳ Request validation and comprehensive error handling
- ⏳ Health checks
- ⏳ Load testing / benchmark scripts

## Technology Stack

- .NET 9.0 with C# 12
- ASP.NET Core Minimal APIs
- Redis (StackExchange.Redis 2.7.33)
- RabbitMQ (RabbitMQ.Client 6.8.1)
- PostgreSQL planned (Npgsql 9.0.0 + Dapper 2.1.35)
- OpenTelemetry for observability
- Swashbuckle for API documentation

## Important Implementation Notes

- **RedisContextStore.ListKeysAsync** (RedisContextStore.cs:20) connects to first Redis endpoint and scans all keys - may be slow on large datasets
- **RabbitMqPublisher** uses fanout exchange that broadcasts to all bound queues (no routing key)
- **Minimal API endpoints** read request body directly using StreamReader for flexibility with data formats
- **SignalR dependencies** are included but not implemented - suggests future real-time push notifications
- **Worker Program.cs** has OpenTelemetry configured but no actual worker service registered yet
