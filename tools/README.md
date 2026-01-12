# SerialMemory Tools

Standalone utilities for database maintenance and operations.

## RebuildIntegrityChain

Rebuilds the tamper-evident hash chain for all memories when integrity verification shows memories as invalid or corrupted.

### When to Use

- Dashboard shows high corruption rate (many invalid memories)
- The canonical form or hash algorithm has changed
- Migrating from an older version without integrity hashes
- After bulk imports or migrations

### Usage

```bash
cd tools/RebuildIntegrityChain

# Show help
dotnet run -- --help

# Rebuild all tenants (uses env vars for connection)
POSTGRES_HOST=localhost POSTGRES_PORT=5432 POSTGRES_PASSWORD=secret dotnet run

# Rebuild specific tenant
dotnet run -- --tenant 019ac272-2239-7407-9f5e-b1d4e4232dc7

# Dry run to preview changes
dotnet run -- --dry-run

# Use BLAKE3 algorithm instead of SHA256
dotnet run -- --algorithm BLAKE3
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_HOST` | localhost | Database host |
| `POSTGRES_PORT` | 5432 | Database port |
| `POSTGRES_USER` | postgres | Database user |
| `POSTGRES_PASSWORD` | postgres | Database password |
| `POSTGRES_DB` | contextdb | Database name |

## Other Tools

### reembed_memories.cs

Re-embeds all memories with a new embedding model. Requires `dotnet-script`.

```bash
dotnet script reembed_memories.cs -- --http-service http://localhost:8765 --force-all
```

### replay_tool.cs

Replays events from the event store. Requires `dotnet-script`.

### embedding_http_service.py

Python HTTP service for generating embeddings.

```bash
python embedding_http_service.py
```

### export_model_to_onnx.py

Exports a HuggingFace model to ONNX format.
