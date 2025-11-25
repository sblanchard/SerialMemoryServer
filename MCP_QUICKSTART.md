# Python MCP Server - Quick Start

## What You Just Set Up

The Python MCP server is a **temporal knowledge graph memory system** that allows AI agents (like Claude Desktop) to:

1. **Store memories** with automatic entity extraction
2. **Search semantically** using vector embeddings
3. **Track relationships** between entities
4. **Multi-hop reasoning** across the knowledge graph

## Setup Complete! ✅

All dependencies are installed:
- ✅ PyTorch (CPU-only)
- ✅ sentence-transformers (for embeddings)
- ✅ spaCy + en_core_web_sm model (for NER)
- ✅ PostgreSQL drivers + pgvector
- ✅ MCP protocol library

## How to Use with Claude Desktop

### 1. Find Your Config File

- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
- **Mac**: `~/Library/Application Support/Claude/claude_desktop_config.json`

### 2. Add This Configuration

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

**IMPORTANT**: Update the paths if your installation is in a different location!

### 3. Restart Claude Desktop

Completely quit and restart Claude Desktop. The MCP tools will appear in the tool picker.

## Available MCP Tools

Once configured, you'll have these tools in Claude Desktop:

### memory_ingest
Store a new memory with automatic entity extraction:
```
Example: "I'm working on a C# microservices project with RabbitMQ and Redis for interview prep."
Extracts: C#, microservices, RabbitMQ, Redis, interview
Creates: Relationships between entities
```

### memory_search
Search your memories semantically or with full-text:
```
modes: semantic (vector search), text (SQL), hybrid (both)
Example: "What programming languages am I learning?"
Returns: Relevant memories with similarity scores
```

### memory_multi_hop_search
Traverse the knowledge graph to find connected information:
```
Example: "Start from 'RabbitMQ' and follow 2 hops"
Discovers: RabbitMQ → projects → collaborators → related_tools
```

### memory_about_user
Retrieve user persona (preferences, skills, goals):
```
Returns: Structured data about your skills, preferences, background
```

### Session Management
- `initialise_conversation_session` - Start tracking context
- `end_conversation_session` - End session

## Test Prompts for Claude Desktop

Once configured, try these:

```
1. "Remember: I'm preparing for interviews focused on .NET microservices,
   MassTransit, SignalR, and distributed systems."

2. "What do you remember about my technical skills?"

3. "Using multi-hop search, find everything related to 'microservices' in my memories."

4. "What are my career goals based on what I've told you?"
```

## Architecture

```
Claude Desktop
      ↓ (MCP STDIO protocol)
Python MCP Server
      ↓
┌──────────────────────────────────────┐
│ sentence-transformers (embeddings)   │
│ spaCy (entity extraction)            │
│ pgvector (vector similarity)         │
└──────────────────────────────────────┘
      ↓
PostgreSQL (knowledge graph)
  - memories (with 384-dim embeddings)
  - entities (PERSON, ORG, GPE, etc.)
  - entity_relationships
  - user_personas
```

## Two Independent Systems

Your project now has **two independent systems**:

### 1. Python MCP Server (PRIMARY)
- **Purpose**: AI memory & knowledge graph
- **Integration**: Claude Desktop (via MCP protocol)
- **Features**: Semantic search, entity extraction, multi-hop reasoning
- **Database**: PostgreSQL (port 5434) with pgvector

### 2. .NET Services (LEARNING)
- **Purpose**: Microservices demo for interview prep
- **Components**: API + Worker + Redis + RabbitMQ
- **Features**: MassTransit, SignalR, event-driven architecture
- **Database**: PostgreSQL (port 5434) for event persistence

**They can run simultaneously!** They both use the same PostgreSQL instance but different tables.

## Troubleshooting

### MCP tools don't appear in Claude Desktop
1. Verify config file location
2. Check JSON syntax (use jsonlint.com)
3. Verify paths are absolute (not relative)
4. Check Claude Desktop logs: Help → Show Logs
5. Completely restart Claude Desktop (not just window)

### Database connection errors
```bash
# Check PostgreSQL is running
docker ps | findstr postgres

# Test connection
psql -h localhost -p 5434 -U postgres -d contextdb -c "\dt"
```

### Python errors
```bash
# Verify environment
cd SerialMemory.Mcp.Python
.\venv\Scripts\python.exe -m pip list | findstr "mcp spacy sentence"

# Reinstall if needed
.\venv\Scripts\python.exe -m pip install --upgrade mcp spacy sentence-transformers
```

## Next Steps

1. **Configure Claude Desktop** with the JSON above
2. **Restart Claude Desktop**
3. **Test the MCP tools** with the example prompts
4. **Build your knowledge graph** by storing memories
5. **Continue with .NET microservices** if you want to keep learning

## Documentation

- Full setup guide: `SETUP_MCP.md`
- .NET debugging guide: `BREAKPOINT_CHEATSHEET.md`
- Project overview: `CLAUDE.md`

## Questions?

- **"How do I start the MCP server?"** → Claude Desktop starts it automatically
- **"Can I use this without Claude Desktop?"** → Yes, but you'd need to write your own MCP client
- **"Is this different from the .NET MCP server?"** → Yes, the Python version is newer and has full knowledge graph features
- **"Will this interfere with my .NET work?"** → No, they're completely independent systems

## What's Powerful About This

When Claude Desktop has the MCP server configured:
- **Persistent memory** across conversations
- **Semantic understanding** of your context
- **Relationship tracking** between concepts
- **Multi-hop reasoning** to discover connections
- **User persona learning** over time

It's like giving Claude a **long-term memory system** powered by a knowledge graph!
