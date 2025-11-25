# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SerialMemoryServer is a **CORE-like temporal knowledge graph memory system** inspired by [getcore.me](https://getcore.me/). It provides:

- Temporal knowledge graph with semantic search
- Entity extraction and relationship tracking
- Semantic embeddings (384-dim vectors)
- PostgreSQL + pgvector for storage
- MCP protocol for AI agent integration
- **CORE import capability** for migrating from getcore.me

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
- **PostgreSQL**: localhost:5432 (postgres/postgres, db: contextdb)
- **Redis**: localhost:6379
- **RabbitMQ Management**: http://localhost:15672 (guest/guest)

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

- `POSTGRES_HOST` - PostgreSQL host (default: localhost)
- `POSTGRES_PORT` - PostgreSQL port (default: 5432)
- `POSTGRES_USER` - Database user (default: postgres)
- `POSTGRES_PASSWORD` - Database password (default: postgres)
- `POSTGRES_DB` - Database name (default: contextdb)
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

## Embedding Service Setup

The C# MCP server requires an HTTP embedding service. Start one with:

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
model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')

@app.post("/embed")
def embed(request: dict):
    return {"embedding": model.encode(request["text"]).tolist()}

@app.post("/embed-batch")
def embed_batch(request: dict):
    return {"embeddings": [e.tolist() for e in model.encode(request["texts"])]}

if __name__ == "__main__":
    uvicorn.run(app, port=8765)
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

## Current Implementation Status

**Completed:**
- ✅ Full C# MCP Server with knowledge graph tools
- ✅ KnowledgeGraphService orchestration layer
- ✅ PostgresKnowledgeGraphStore (complete CRUD)
- ✅ Pattern-based entity extraction
- ✅ HTTP embedding service integration
- ✅ CORE import functionality
- ✅ Multi-hop graph traversal
- ✅ User persona management
- ✅ Conversation session tracking
- ✅ PostgreSQL schema with pgvector

**Architecture:**
- ✅ Clean Architecture (Core → Infrastructure → Mcp)
- ✅ Async/await throughout
- ✅ Connection pooling
- ✅ Proper error handling and logging
