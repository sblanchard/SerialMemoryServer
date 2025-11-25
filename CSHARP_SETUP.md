# C# Full Solution Setup Guide

This guide explains how to set up and run the complete C# CORE-like knowledge graph memory system.

## Overview

The C# solution consists of:

- **SerialMemory.Core** - Domain models (Memory, Entity, Relationships, etc.)
- **SerialMemory.ML** - ML services (ONNX embeddings, entity extraction)
- **SerialMemory.Infrastructure** - Data access (PostgreSQL + pgvector, Redis, RabbitMQ)
- **SerialMemory.Mcp** - MCP STDIO server for AI agent integration
- **SerialMemory.Api** - REST API with SignalR (optional)
- **SerialMemory.Worker** - Background worker (optional)

## Prerequisites

- **.NET 9 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Docker Desktop** - For PostgreSQL, Redis, RabbitMQ
- **Python 3.11+** - For exporting ONNX models
- **Visual Studio 2022 or Rider** (optional, for debugging)

## Step 1: Start Infrastructure

```bash
# Start PostgreSQL (with pgvector), Redis, RabbitMQ
docker compose up -d postgres redis rabbitmq

# Verify services are running
docker compose ps
```

## Step 2: Export Sentence-Transformers to ONNX

The C# embedding service uses ONNX Runtime to run sentence-transformers models. You need to export the Python model to ONNX format first.

### Option A: Use Python Export Script (Recommended)

```bash
# Install Python dependencies
pip install sentence-transformers torch onnx

# Run export script
python tools/export_model_to_onnx.py
```

This creates: `tools/onnx_models/all-MiniLM-L6-v2/model.onnx`

### Option B: Use Pre-Exported Model

Download from [Hugging Face ONNX models](https://huggingface.co/models?library=onnx) or use Optimum:

```bash
pip install optimum[exporters]
optimum-cli export onnx \
  --model sentence-transformers/all-MiniLM-L6-v2 \
  --task feature-extraction \
  onnx_models/all-MiniLM-L6-v2/
```

### Option C: Skip ONNX and Use Python Embedding Service

If ONNX setup is too complex, you can call the Python embedding service from C# via HTTP or subprocess. See "Alternative: Python Embeddings via HTTP" below.

## Step 3: Configure the MCP Server

Create `SerialMemory.Mcp/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Port=5432;Database=contextdb;Username=postgres;Password=postgres"
  },
  "ML": {
    "OnnxModelPath": "D:\\DEV\\SavvyContextServer\\tools\\onnx_models\\all-MiniLM-L6-v2\\model.onnx",
    "TokenizerPath": "D:\\DEV\\SavvyContextServer\\tools\\onnx_models\\all-MiniLM-L6-v2\\tokenizer.json"
  }
}
```

**Update the paths** to match your actual locations.

## Step 4: Build the Solution

```bash
# Restore packages
dotnet restore

# Build entire solution
dotnet build SerialMemoryServer.sln

# Or build individual projects
dotnet build SerialMemory.Core
dotnet build SerialMemory.ML
dotnet build SerialMemory.Infrastructure
dotnet build SerialMemory.Mcp
```

## Step 5: Run the MCP Server

```bash
dotnet run --project SerialMemory.Mcp
```

You should see output like:
```
info: Starting Serial Memory MCP Server (C#)
info: Database: Host=localhost;Port=5432;Database=contextdb
info: Loading ONNX model from: ...model.onnx
info: Embedding service initialized (384 dimensions)
info: Entity extraction service initialized
info: MCP server listening on STDIO
```

## Step 6: Configure Claude Desktop

Edit your Claude Desktop config file:

**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

Add:

```json
{
  "mcpServers": {
    "serial-memory-csharp": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\DEV\\SavvyContextServer\\SerialMemory.Mcp"],
      "cwd": "D:\\DEV\\SavvyContextServer",
      "env": {}
    }
  }
}
```

**Update `cwd`** to match your installation directory.

## Step 7: Test It!

Restart Claude Desktop and try:

```
Use the memory_ingest tool to store:
"I met Sarah Johnson at the AI conference in San Francisco. She's a researcher at Stanford working on neural networks."
```

Claude should extract entities (Sarah Johnson, Stanford, San Francisco) and relationships.

```
Use the memory_search tool to find:
"Who did I meet in San Francisco?"
```

Claude should return the memory with high similarity.

## Alternative: Python Embeddings via HTTP

If ONNX is too complex, you can keep using the Python embedding service and call it from C# via HTTP or subprocess.

### Option 1: HTTP Wrapper Service

Create a simple Flask/FastAPI service that wraps the Python embeddings:

```python
# embedding_service.py
from fastapi import FastAPI
from sentence_transformers import SentenceTransformer

app = FastAPI()
model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')

@app.post("/embed")
def embed(text: str):
    return {"embedding": model.encode(text).tolist()}

# Run with: uvicorn embedding_service:app --port 8000
```

Then in C#:

```csharp
public class HttpEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _client = new();

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default)
    {
        var response = await _client.PostAsJsonAsync(
            "http://localhost:8000/embed",
            new { text },
            ct
        );
        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        return result.Embedding;
    }

    record EmbedResponse(float[] Embedding);
}
```

### Option 2: Subprocess Call

Call Python directly from C#:

```csharp
public class PythonEmbeddingService : IEmbeddingService
{
    public async Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"-c \"from sentence_transformers import SentenceTransformer; model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2'); print(list(model.encode('{text}')))\"",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        var output = await process!.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        // Parse output and convert to float[]
        return ParsePythonList(output);
    }
}
```

## Optional Services

### Run REST API (Optional)

If you want HTTP endpoints:

```bash
dotnet run --project SerialMemory.Api
```

Access Swagger at: http://localhost:5000/swagger

### Run Worker (Optional)

If you want event processing:

```bash
dotnet run --project SerialMemory.Worker
```

## Architecture

```
Claude Desktop
    ↓ (STDIO)
SerialMemory.Mcp (MCP Server)
    ↓
SerialMemory.ML (Embeddings + NER)
    ↓
SerialMemory.Infrastructure (PostgreSQL + pgvector)
    ↓
PostgreSQL Database (Knowledge Graph)
```

## Available MCP Tools

| Tool | Description |
|------|-------------|
| `memory_search` | Semantic/text/hybrid search with entities |
| `memory_ingest` | Add memories with entity extraction |
| `memory_about_user` | Get user persona |
| `initialise_conversation_session` | Start session tracking |
| `end_conversation_session` | End current session |
| `memory_multi_hop_search` | Multi-hop graph traversal |
| `get_integrations` | List external tools |

## Database Schema

8 tables forming the knowledge graph:
- `memories` - Episodes with vector(384) embeddings
- `entities` - Extracted entities (PERSON, ORG, GPE, etc.)
- `entity_relationships` - Directed edges
- `memory_entities` - Many-to-many links
- `user_personas` - User attributes
- `conversation_sessions` - Session tracking
- `integrations` + `integration_actions` - External tools

All defined in `ops/init.sql`.

## Troubleshooting

### "ONNX model not found"

Ensure you ran the Python export script and the path in `appsettings.json` is correct.

### "Database connection failed"

Check PostgreSQL is running:
```bash
docker compose ps postgres
docker compose logs postgres
```

### "Could not load pgvector extension"

Ensure you're using `pgvector/pgvector:pg17` Docker image (check `docker-compose.yml`).

### "Entity extraction not working"

The pattern-based extractor uses regex. For better results:
- Use proper names with capital letters
- Use full context sentences
- Consider integrating Azure Cognitive Services or Stanford NLP

### ONNX is too complicated

Use the Python HTTP wrapper (Option 1 above) or subprocess approach (Option 2). These are simpler but slightly slower.

## Performance

- **Semantic search**: <100ms for 10k memories (with pgvector IVFFlat index)
- **Entity extraction**: <50ms per memory (regex-based)
- **ONNX embedding**: ~20ms per text on CPU, ~5ms on GPU

## Next Steps

1. **Ingest sample data** - Build up your knowledge graph
2. **Test semantic search** - Query with natural language
3. **Try multi-hop reasoning** - Complex relationship queries
4. **Customize entity patterns** - Add domain-specific entities
5. **Integrate with other tools** - Cursor, Windsurf, VS Code

## Comparison: C# vs Python

| Feature | C# Solution | Python Solution |
|---------|-------------|-----------------|
| ONNX embedding | ✅ | ❌ (native transformers) |
| Pattern NER | ✅ | ❌ (spaCy NLP) |
| Performance | Fast (compiled) | Moderate (interpreted) |
| Setup complexity | High (ONNX export) | Low (pip install) |
| ML ecosystem | Limited | Extensive |
| Type safety | Strong | Dynamic |
| Debugging | Excellent | Good |

**Recommendation**: If you're comfortable with Python and ML, use the Python solution. If you need a pure C# stack or have C# expertise, use this solution.

## Resources

- [ONNX Runtime Documentation](https://onnxruntime.ai/)
- [pgvector GitHub](https://github.com/pgvector/pgvector)
- [Sentence Transformers](https://www.sbert.net/)
- [MCP Documentation](https://modelcontextprotocol.io/)

## Support

For issues:
1. Check this troubleshooting guide
2. Review logs from `dotnet run`
3. Check Docker logs: `docker compose logs`
4. Open GitHub issue with error messages
