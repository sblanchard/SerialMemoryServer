# Python MCP Server Setup Guide

## Prerequisites

- Python 3.11+ (you have 3.13.9 ✓)
- PostgreSQL with pgvector extension running (port 5434)
- ~2GB disk space for ML models

## Step 1: Install Python Dependencies

```bash
cd SerialMemory.Mcp.Python

# Activate virtual environment
.\venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt

# Download spaCy language model
python -m spacy download en_core_web_sm
```

## Step 2: Configure Environment

Create `.env` file in `SerialMemory.Mcp.Python/`:

```env
POSTGRES_HOST=localhost
POSTGRES_PORT=5434
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
POSTGRES_DB=contextdb
EMBEDDING_MODEL=sentence-transformers/all-MiniLM-L6-v2
SPACY_MODEL=en_core_web_sm
```

## Step 3: Verify Database Schema

The PostgreSQL database should have the knowledge graph tables from `ops/init.sql`:
- memories
- entities
- entity_relationships
- memory_entities
- user_personas
- conversation_sessions
- integrations
- integration_actions

Check if tables exist:
```bash
docker exec -it postgres psql -U postgres -d contextdb -c "\dt"
```

## Step 4: Test MCP Server Locally

Run the server to verify it works:

```bash
cd SerialMemory.Mcp.Python
.\venv\Scripts\python.exe -m src.main
```

You should see:
```
Starting Serial Memory MCP Server (CORE-like)
Database: postgresql://postgres:***@localhost:5434/contextdb
Embedding model: sentence-transformers/all-MiniLM-L6-v2
spaCy model: en_core_web_sm
Initializing database pool...
Initializing embedding service...
Initializing entity extraction service...
All services initialized successfully
```

Press Ctrl+C to stop after verifying.

## Step 5: Configure Claude Desktop

Find your Claude Desktop config file:
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- Mac: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Linux: `~/.config/claude/claude_desktop_config.json`

Add the MCP server configuration:

```json
{
  "mcpServers": {
    "serial-memory": {
      "command": "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp.Python\\venv\\Scripts\\python.exe",
      "args": ["-m", "src.main"],
      "cwd": "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp.Python",
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5434",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb"
      }
    }
  }
}
```

**IMPORTANT**: Update the paths to match your installation directory!

## Step 6: Restart Claude Desktop

1. Completely quit Claude Desktop
2. Restart it
3. Look for MCP tools in the tool picker:
   - memory_search
   - memory_ingest
   - memory_about_user
   - initialise_conversation_session
   - end_conversation_session
   - memory_multi_hop_search
   - get_integrations

## MCP Tools Available

### memory_search
Search memories using semantic/text/hybrid search
```
Query: "machine learning projects"
Mode: hybrid (semantic + text)
Returns: Relevant memories with entities
```

### memory_ingest
Add new memory with auto entity extraction
```
Content: "I built a Python REST API using FastAPI and PostgreSQL"
Extracts: entities (Python, FastAPI, PostgreSQL)
Creates: relationships between entities
```

### memory_multi_hop_search
Traverse knowledge graph connections
```
Query: "python"
Hops: 2
Discovers: python → projects → collaborators → related_technologies
```

### memory_about_user
Get user persona (preferences, skills, background)

### Session Management
- initialise_conversation_session - Start tracking context
- end_conversation_session - End session

## Testing with Claude Desktop

Once configured, try these prompts in Claude Desktop:

1. **Store a memory:**
   ```
   Remember: I'm a .NET developer learning microservices with C#,
   RabbitMQ, Redis, and MassTransit for job interviews.
   ```

2. **Search memories:**
   ```
   What do you remember about my .NET projects?
   ```

3. **Multi-hop reasoning:**
   ```
   Using multi-hop search, find everything connected to "RabbitMQ" in my memories.
   ```

## Architecture

```
Claude Desktop → STDIO → MCP Server
                           ↓
    ┌──────────────────────┴──────────────────────┐
    │                                              │
    ▼                                              ▼
sentence-transformers                          spaCy NER
(384-dim embeddings)                    (entity extraction)
    │                                              │
    └──────────────────────┬──────────────────────┘
                           ▼
                    PostgreSQL + pgvector
                  (vector similarity search)
```

## Troubleshooting

### "Database connection failed"
- Check PostgreSQL is running: `docker ps | findstr postgres`
- Verify port 5434 is correct
- Test connection: `psql -h localhost -p 5434 -U postgres -d contextdb`

### "Module not found: mcp"
- Activate venv: `.\venv\Scripts\activate`
- Reinstall: `pip install -r requirements.txt`

### "spaCy model not found"
- Download model: `python -m spacy download en_core_web_sm`

### Claude Desktop doesn't show MCP tools
- Check config file path is correct
- Verify JSON syntax is valid
- Check Claude Desktop logs (Help → Show Logs)
- Completely restart Claude Desktop

## What's Next?

Once the MCP server is working:
1. Test it with Claude Desktop to store/search memories
2. Use it to build your personal knowledge graph
3. The .NET services (API + Worker) are separate - you can run them alongside for learning microservices patterns

## Difference from .NET Services

| Component | Purpose | Technology |
|-----------|---------|------------|
| **Python MCP** | AI memory system | sentence-transformers, spaCy, pgvector |
| **.NET API** | REST endpoints, SignalR | ASP.NET Core, MassTransit, Redis |
| **.NET Worker** | Event processing | MassTransit, RabbitMQ |

They're **independent** - you can use one, both, or neither!
