# Quick Start: Claude Desktop + MCP

SerialMemory integrates with Claude Desktop via the Model Context Protocol (MCP). This allows Claude to store and retrieve memories across conversations.

## Prerequisites

- Claude Desktop installed
- Docker Desktop running
- .NET 8.0 SDK (or pre-built executable)

## Step 1: Start SerialMemory

```bash
# Clone and start the stack
git clone https://github.com/serialmemory/serialmemory.git
cd serialmemory
docker compose up -d

# Run bootstrap to create demo tenant
./scripts/bootstrap-dev.ps1  # Windows
./scripts/bootstrap-dev.sh   # Linux/Mac
```

## Step 2: Configure Claude Desktop

Edit your Claude Desktop configuration file:

**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

Add the SerialMemory MCP server:

```json
{
  "mcpServers": {
    "serialmemory": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\path\\to\\SerialMemoryServer\\SerialMemory.Mcp"],
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

Or use a pre-built executable:

```json
{
  "mcpServers": {
    "serialmemory": {
      "command": "D:\\path\\to\\SerialMemory.Mcp.exe",
      "args": [],
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb",
        "OLLAMA_BASE_URL": "http://localhost:11434"
      }
    }
  }
}
```

## Step 3: Restart Claude Desktop

Close and reopen Claude Desktop to load the new MCP server.

## Step 4: Test the Integration

Try these prompts in Claude:

**Store a memory:**
```
Remember that I prefer TypeScript over JavaScript for new projects.
```

**Search memories:**
```
What do you remember about my programming preferences?
```

**Set user persona:**
```
I'm a backend developer focused on distributed systems.
```

## Available MCP Tools

| Tool | Description |
|------|-------------|
| `memory_search` | Search memories semantically |
| `memory_ingest` | Store new memories |
| `memory_update` | Update existing memory |
| `memory_delete` | Soft-delete memory |
| `memory_about_user` | Get user persona |
| `set_user_persona` | Set persona attributes |
| `memory_multi_hop_search` | Graph traversal search |
| `get_graph_statistics` | Knowledge graph stats |

## Troubleshooting

**MCP server not connecting:**
- Check Docker containers are running: `docker ps`
- Verify PostgreSQL is accessible: `docker exec serialmemory-postgres pg_isready`
- Check Ollama has the model: `docker exec serialmemory-ollama ollama list`

**Embeddings not working:**
- Pull the embedding model: `docker exec serialmemory-ollama ollama pull nomic-embed-text`

**Permission errors:**
- Ensure the MCP executable path is correct
- Check file permissions on Windows

## Next Steps

- [Plans and Limits](./05-plans-and-limits.md)
- [Data Lifecycle](./06-data-lifecycle.md)
- [Self-Hosting Guide](./07-self-hosting.md)
