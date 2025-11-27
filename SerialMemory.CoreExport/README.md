# SerialMemory CORE Export Tool

A robust console application for exporting all your data from CORE (core.heysol.ai) into local structured files.

## Features

✅ **Complete Data Export** - Exports all workspaces, memories, metadata, and relationships
✅ **Resume Support** - Safely restart after interruption or failure
✅ **Pagination** - Handles large datasets without running out of memory
✅ **Retry Logic** - Exponential backoff for rate limits (429) and server errors (5xx)
✅ **Multiple Formats** - JSON (canonical) and CSV (flat view)
✅ **Telemetry** - Detailed logging to console and file
✅ **Graceful Shutdown** - Ctrl+C saves progress and allows resuming

## Prerequisites

- .NET 10.0 or newer
- CORE API key from core.heysol.ai

## Configuration

Edit `appsettings.json` before running:

```json
{
  "Core": {
    "BaseUrl": "https://core.heysol.ai/api",
    "ApiKey": "YOUR_API_KEY_HERE",
    "ExportPath": "./exports",
    "PageSize": 100,
    "IncludeEmbeddings": false
  }
}
```

### Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| `BaseUrl` | CORE API base URL | `https://core.heysol.ai/api` |
| `ApiKey` | Your CORE API key (required) | - |
| `ExportPath` | Directory for exported files | `./exports` |
| `PageSize` | Items per API request | `100` |
| `IncludeEmbeddings` | Include embedding vectors | `false` |

## Usage

### Commands

The tool supports three operations:

```bash
# Export from CORE API to local files
dotnet run --project SerialMemory.CoreExport export

# Import exported CORE data into SerialMemory contextdb
dotnet run --project SerialMemory.CoreExport import

# Do both: export from CORE then import to SerialMemory
dotnet run --project SerialMemory.CoreExport all

# Show help
dotnet run --project SerialMemory.CoreExport help
```

### Environment Variables for Import

When importing, you need to configure:

**Database Connection:**
```bash
POSTGRES_HOST=localhost
POSTGRES_PORT=5432
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
POSTGRES_DB=contextdb
```

**Embedding Service (choose one):**

Option 1 - ONNX (recommended):
```bash
ONNX_MODEL_PATH=e:\models\model.onnx
VOCAB_PATH=e:\models\vocab.txt
```

Option 2 - HTTP Service:
```bash
EMBEDDING_SERVICE_URL=http://localhost:8765
```

### Complete Workflow Example

```bash
# 1. Export from CORE
dotnet run --project SerialMemory.CoreExport export

# 2. Start PostgreSQL
docker compose up -d postgres

# 3. Import to SerialMemory
dotnet run --project SerialMemory.CoreExport import

# Or do it all at once:
dotnet run --project SerialMemory.CoreExport all
```

### Run Published Executable

```bash
# Publish self-contained
dotnet publish -c Release -r win-x64 --self-contained

# Run export
./bin/Release/net10.0/win-x64/publish/SerialMemory.CoreExport.exe export

# Run import
./bin/Release/net10.0/win-x64/publish/SerialMemory.CoreExport.exe import

# Run both
./bin/Release/net10.0/win-x64/publish/SerialMemory.CoreExport.exe all
```

## Output Structure

The tool creates the following structure:

```
exports/
├── export-state.json                    # Resume state
├── workspace-{id}/
│   ├── memories.json                    # Full JSON export
│   ├── memories.csv                     # Flat CSV export
│   └── metadata.json                    # Workspace metadata
└── workspace-{id2}/
    └── ...
```

### JSON Export Format

```json
{
  "workspaceId": "ws_abc123",
  "workspaceName": "MainWorkspace",
  "exportedAtUtc": "2025-11-25T12:00:00Z",
  "memoryCount": 512,
  "memories": [
    {
      "id": "mem_xyz",
      "content": "Memory content here",
      "createdAt": "2025-11-20T10:00:00Z",
      "metadata": { ... },
      "relationships": [ ... ]
    }
  ]
}
```

### CSV Export Format

| Id | Content | CreatedAt | UpdatedAt | Metadata |
|----|---------|-----------|-----------|----------|
| mem_xyz | Memory content | 2025-11-20... | 2025-11-21... | {...} |

## Resume Support

The tool automatically saves progress in `exports/export-state.json`:

```json
{
  "lastWorkspace": "ws_abc123",
  "lastCursor": "cursor_456",
  "completedWorkspaces": ["ws_001", "ws_002"],
  "lastUpdated": "2025-11-25T12:34:56Z"
}
```

If interrupted:
1. Press Ctrl+C to gracefully shutdown (saves state)
2. Run again - it will resume from where it left off
3. To start fresh, delete `exports/export-state.json`

## Error Handling

The tool handles:

- **HTTP 429 (Rate Limit)** - Exponential backoff with jitter
- **5xx Server Errors** - Retry with exponential backoff (max 5 retries)
- **Timeouts** - Automatic retry with backoff
- **Network Issues** - Saves state and can resume

## Logging

Logs are written to:
- **Console** - Real-time progress with timestamps
- **File** - `logs/core-export-{date}.log` (daily rolling)

Example log output:
```
[2025-11-25 12:00:00] [INF] CORE Export Tool Starting
[2025-11-25 12:00:01] [INF] Found 3 workspaces to export
[2025-11-25 12:00:02] [INF] Exporting workspace: MainWorkspace (ws_123)
[2025-11-25 12:00:05] [INF] Fetched 100 memories (total: 100)
[2025-11-25 12:00:08] [INF] Fetched 100 memories (total: 200)
[2025-11-25 12:00:10] [INF] Completed fetching 250 memories for workspace MainWorkspace
[2025-11-25 12:00:11] [INF] Written JSON export: ./exports/workspace-ws_123/memories.json
[2025-11-25 12:00:12] [INF] Written CSV export: ./exports/workspace-ws_123/memories.csv
[2025-11-25 12:00:12] [INF] Export completed successfully!
```

## Telemetry

The tool logs:
- Requests per second
- Items exported per workspace
- Failures per endpoint
- Retry attempts and backoff delays

## Graceful Shutdown

Press `Ctrl+C` at any time to:
1. Complete the current API request
2. Save progress to `export-state.json`
3. Safely exit

Resume by running the tool again.

## Security Notes

- **API Key** - Never commit `appsettings.json` with your real API key
- **Sensitive Data** - Exported files contain your CORE data - protect them accordingly

## Troubleshooting

### "Cannot read properties of undefined"
- Check that `ApiKey` is set in `appsettings.json`

### "401 Unauthorized"
- Verify your API key is correct and active

### "429 Too Many Requests"
- The tool automatically handles this with exponential backoff
- Consider reducing `PageSize` in config

### Export hangs
- Check logs in `logs/` directory for details
- Verify network connectivity to core.heysol.ai

## Architecture

The tool uses:
- **CoreApiClient** - HTTP client with retry logic and pagination
- **WorkspaceExporter** - Orchestrates export with resume support
- **Serilog** - Structured logging to console and file
- **CsvHelper** - CSV generation
- **System.Text.Json** - JSON serialization

## Development

Built with:
- .NET 10.0
- C# 12
- Async/await throughout
- Dependency injection
- Structured logging

## License

Part of the SerialMemoryServer project.
