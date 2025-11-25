# Setup Guide - SerialMemoryServer

This guide will help you set up the CORE-like knowledge graph memory system.

## Prerequisites

- **Python 3.11+** (for MCP server)
- **Docker & Docker Compose** (for infrastructure)
- **Git** (for cloning the repo)
- **Claude Desktop** or other MCP client (for testing)

## Step-by-Step Setup

### 1. Clone the Repository

```bash
git clone <repository-url>
cd SerialMemoryServer
```

### 2. Start Infrastructure Services

Start PostgreSQL with pgvector, Redis, and RabbitMQ:

```bash
docker compose up -d postgres redis rabbitmq
```

Verify services are running:

```bash
docker compose ps
```

You should see:
- `postgres` on port 5432
- `redis` on port 6379
- `rabbitmq` on ports 5672 (AMQP) and 15672 (Management UI)

### 3. Verify Database Schema

The PostgreSQL database should automatically initialize with the schema from `ops/init.sql`.

Check the database:

```bash
docker compose exec postgres psql -U postgres -d contextdb -c "\dt"
```

You should see tables: `memories`, `entities`, `entity_relationships`, `memory_entities`, `user_personas`, `conversation_sessions`, `integrations`, `integration_actions`.

### 4. Set Up Python Environment

Navigate to the Python MCP server directory:

```bash
cd SerialMemory.Mcp.Python
```

Create a virtual environment (recommended):

```bash
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate
```

Install dependencies:

```bash
pip install -r requirements.txt
```

Download the spaCy language model:

```bash
python -m spacy download en_core_web_sm
```

### 5. Configure Environment Variables

Create a `.env` file in `SerialMemory.Mcp.Python/` (or use the example):

```bash
cp src/.env.example .env
```

Edit `.env` if needed (default values work with Docker Compose):

```env
POSTGRES_HOST=localhost
POSTGRES_PORT=5432
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
POSTGRES_DB=contextdb
EMBEDDING_MODEL=sentence-transformers/all-MiniLM-L6-v2
SPACY_MODEL=en_core_web_sm
```

### 6. Test the MCP Server

Run the MCP server directly to verify it works:

```bash
python -m src.main
```

You should see logs indicating:
- Database pool initialized
- Embedding model loaded
- spaCy model loaded
- MCP server started

Press `Ctrl+C` to stop.

## Configure Claude Desktop

### 7. Add MCP Server to Claude Desktop

Edit your Claude Desktop configuration file:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
**Linux:** `~/.config/Claude/claude_desktop_config.json`

Add the MCP server configuration:

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

**Important:** Update the `cwd` path to match your actual installation directory.

### 8. Restart Claude Desktop

Completely quit and restart Claude Desktop for the changes to take effect.

### 9. Verify MCP Integration

In Claude Desktop, you should see the MCP server connected. Try these commands:

**Test 1: Ingest a memory**
```
Use the memory_ingest tool to store this memory:
"I met Sarah Johnson at the AI conference in San Francisco. She's a researcher at Stanford working on neural networks."
```

Claude should respond with confirmation, showing extracted entities (Sarah Johnson, Stanford, San Francisco, AI conference) and relationships.

**Test 2: Search for the memory**
```
Use the memory_search tool to find: "Who did I meet in San Francisco?"
```

Claude should return the memory you just added with high similarity.

**Test 3: Check user persona**
```
Use the memory_about_user tool to see what you know about me.
```

(This will be empty initially until you ingest memories with preferences/skills)

## Troubleshooting

### Issue: "Database pool not initialized"

**Solution:** Ensure PostgreSQL is running:
```bash
docker compose ps postgres
```

If not running:
```bash
docker compose up -d postgres
```

### Issue: "Could not connect to database"

**Solution:** Check PostgreSQL logs:
```bash
docker compose logs postgres
```

Verify connection settings in `.env` match Docker Compose settings.

### Issue: "spaCy model not found"

**Solution:** Download the model:
```bash
python -m spacy download en_core_web_sm
```

### Issue: "Module 'vector' not found in PostgreSQL"

**Solution:** Ensure you're using the pgvector image:
```bash
docker compose exec postgres psql -U postgres -d contextdb -c "CREATE EXTENSION IF NOT EXISTS vector;"
```

### Issue: "MCP server not appearing in Claude Desktop"

**Solutions:**
1. Check `cwd` path in `claude_desktop_config.json` is correct
2. Ensure Python virtual environment has all dependencies
3. Check Claude Desktop logs (usually in `~/Library/Logs/Claude/` on macOS)
4. Try using absolute path for Python command:
   ```json
   "command": "/full/path/to/venv/bin/python"
   ```

## Advanced Configuration

### Use Larger spaCy Model (Better Accuracy)

For improved entity extraction:

```bash
python -m spacy download en_core_web_lg
```

Update `.env`:
```env
SPACY_MODEL=en_core_web_lg
```

### Use Different Embedding Model

For different trade-offs (speed vs accuracy):

```env
# Faster, smaller (default)
EMBEDDING_MODEL=sentence-transformers/all-MiniLM-L6-v2

# Better quality, larger
EMBEDDING_MODEL=sentence-transformers/all-mpnet-base-v2

# Multilingual support
EMBEDDING_MODEL=sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2
```

**Note:** If changing embedding dimension, update `ops/init.sql` vector size and recreate database.

### Enable Redis Caching (Optional)

The current implementation uses PostgreSQL directly. To add Redis caching:

1. Update `src/services/knowledge_graph_service.py` to check Redis before PostgreSQL
2. Set cache TTL based on your needs
3. Ensure Redis is running: `docker compose up -d redis`

## Testing the Knowledge Graph

### Test Entity Extraction

```python
from src.services.entity_extraction_service import entity_service
import asyncio

async def test():
    await entity_service.initialize()
    entities = entity_service.extract_entities(
        "Apple Inc. was founded by Steve Jobs in Cupertino, California in 1976."
    )
    for e in entities:
        print(f"{e.text} ({e.label})")

asyncio.run(test())
```

Expected output:
```
Apple Inc. (ORG)
Steve Jobs (PERSON)
Cupertino (GPE)
California (GPE)
1976 (DATE)
```

### Test Embeddings

```python
from src.services.embedding_service import embedding_service
import asyncio

async def test():
    await embedding_service.initialize()
    embedding = embedding_service.embed_text("Hello world")
    print(f"Embedding dimension: {len(embedding)}")
    print(f"First 5 values: {embedding[:5]}")

asyncio.run(test())
```

### Test Database Queries

```python
from src.db.postgres import db_pool, KnowledgeGraphDB
import asyncio

async def test():
    await db_pool.initialize()
    kg = KnowledgeGraphDB(db_pool)

    # Create a test memory
    memory_id = await kg.create_memory(
        content="Test memory",
        embedding=[0.1] * 384,  # Dummy embedding
        source="test"
    )
    print(f"Created memory: {memory_id}")

    # Retrieve it
    memory = await kg.get_memory_by_id(memory_id)
    print(f"Retrieved: {memory['content']}")

    await db_pool.close()

asyncio.run(test())
```

## Next Steps

1. **Ingest sample data** - Add various memories to build up your knowledge graph
2. **Test multi-hop reasoning** - Try complex queries that require relationship traversal
3. **Monitor performance** - Check query times for semantic search
4. **Customize entity extraction** - Add domain-specific entity types
5. **Integrate with your workflows** - Use across Cursor, Windsurf, etc.

## Performance Tuning

### pgvector Index Tuning

For large datasets (>100k memories), tune the IVFFlat index:

```sql
-- Increase lists for better recall at cost of build time
CREATE INDEX idx_memories_embedding ON memories
USING ivfflat (embedding vector_cosine_ops)
WITH (lists = 500);  -- Increase from 100
```

### Connection Pool Tuning

Edit `src/db/postgres.py`:

```python
self._pool = AsyncConnectionPool(
    conninfo=settings.database_url,
    min_size=5,     # Increase for high concurrency
    max_size=20,    # Increase for high concurrency
    timeout=30,
    max_idle=300,
)
```

### Batch Processing

For ingesting many memories:

```python
# Use batch embedding
texts = ["memory 1", "memory 2", ...]
embeddings = embedding_service.embed_batch(texts)
```

## Resources

- [MCP Documentation](https://modelcontextprotocol.io/)
- [pgvector GitHub](https://github.com/pgvector/pgvector)
- [sentence-transformers Documentation](https://www.sbert.net/)
- [spaCy Documentation](https://spacy.io/)
- [CORE (getcore.me)](https://getcore.me/)

## Support

For issues or questions:
1. Check the troubleshooting section above
2. Review logs: `docker compose logs` and Claude Desktop logs
3. Open an issue on GitHub with logs and error messages
