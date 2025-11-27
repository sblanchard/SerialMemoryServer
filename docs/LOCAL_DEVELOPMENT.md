# Local Development Guide

This guide covers setting up SerialMemory for local development and self-hosting.

## Quick Start

### Prerequisites

- **Docker Desktop** - [Install Docker](https://www.docker.com/products/docker-desktop)
- **.NET 8 SDK** (for MCP server development) - [Install .NET](https://dotnet.microsoft.com/download)
- **Node.js 18+** (for TypeScript SDK) - [Install Node.js](https://nodejs.org/)

### One-Command Setup

```bash
# Windows (PowerShell)
.\dev-bootstrap.ps1

# Linux/macOS
./dev-bootstrap.sh
```

This script will:
1. Start PostgreSQL with pgvector, Redis, and Ollama
2. Apply all database migrations
3. Seed demo data
4. Pull the embedding model
5. Generate sample credentials
6. Output configuration for Claude Desktop

### Manual Setup

If you prefer manual control:

```bash
# Start infrastructure
docker compose -f docker-compose.dev.yml up -d

# Wait for Postgres to be ready
docker compose -f docker-compose.dev.yml logs -f postgres

# Pull embedding model
docker exec serialmemory-ollama ollama pull nomic-embed-text

# Run MCP server locally
dotnet run --project SerialMemory.Mcp
```

## Services

| Service | URL | Description |
|---------|-----|-------------|
| PostgreSQL | `localhost:5432` | Database with pgvector |
| Redis | `localhost:6379` | Caching and rate limiting |
| Ollama | `http://localhost:11434` | Local embedding generation |
| API | `http://localhost:5000` | REST API (if running) |
| Dashboard | `http://localhost:5001` | Tenant dashboard API |
| Prometheus | `http://localhost:9090` | Metrics |
| Grafana | `http://localhost:3001` | Dashboards (admin/admin) |

## Database Access

```bash
# Connect via psql
docker exec -it serialmemory-postgres psql -U postgres -d contextdb

# Or use any PostgreSQL client:
# Host: localhost
# Port: 5432
# User: postgres
# Password: postgres
# Database: contextdb
```

### Useful Queries

```sql
-- Count memories by tenant
SELECT tenant_id, COUNT(*) FROM memories GROUP BY tenant_id;

-- View entities
SELECT name, entity_type, COUNT(*) as mentions
FROM entities
GROUP BY name, entity_type
ORDER BY mentions DESC
LIMIT 20;

-- Check RLS is working
SET app.tenant_id = '00000000-0000-0000-0000-000000000000';
SELECT * FROM memories LIMIT 5;
```

## Running the MCP Server

### Option 1: Direct (Development)

```bash
# From repository root
dotnet run --project SerialMemory.Mcp
```

### Option 2: With Hot Reload

```bash
dotnet watch run --project SerialMemory.Mcp
```

### Option 3: Published Binary

```bash
# Build release binary
dotnet publish SerialMemory.Mcp -c Release -r win-x64 --self-contained

# Run published binary
./SerialMemory.Mcp/bin/Release/net9.0/win-x64/publish/SerialMemory.Mcp.exe
```

## Claude Desktop Integration

Add to your `claude_desktop_config.json`:

### Using dotnet run (Development)

```json
{
  "mcpServers": {
    "serialmemory": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp"],
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb",
        "OLLAMA_BASE_URL": "http://localhost:11434",
        "OLLAMA_EMBEDDING_MODEL": "nomic-embed-text",
        "SERIALMEMORY_MODE": "self-hosted"
      }
    }
  }
}
```

### Using Published Binary (Production)

```json
{
  "mcpServers": {
    "serialmemory": {
      "command": "D:\\DEV\\SerialMemoryServer\\SerialMemory.Mcp\\bin\\Release\\net9.0\\win-x64\\publish\\SerialMemory.Mcp.exe",
      "env": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5432",
        "POSTGRES_USER": "postgres",
        "POSTGRES_PASSWORD": "postgres",
        "POSTGRES_DB": "contextdb",
        "OLLAMA_BASE_URL": "http://localhost:11434",
        "OLLAMA_EMBEDDING_MODEL": "nomic-embed-text",
        "SERIALMEMORY_MODE": "self-hosted"
      }
    }
  }
}
```

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `POSTGRES_HOST` | PostgreSQL host | `localhost` |
| `POSTGRES_PORT` | PostgreSQL port | `5432` |
| `POSTGRES_USER` | Database user | `postgres` |
| `POSTGRES_PASSWORD` | Database password | `postgres` |
| `POSTGRES_DB` | Database name | `contextdb` |
| `OLLAMA_BASE_URL` | Ollama API URL | `http://localhost:11434` |
| `OLLAMA_EMBEDDING_MODEL` | Embedding model | `nomic-embed-text` |
| `SERIALMEMORY_MODE` | Auth mode | `saas` or `self-hosted` |
| `SERIALMEMORY_DEFAULT_TENANT` | Default tenant ID | Required if self-hosted |

## Self-Hosted vs SaaS Mode

### Self-Hosted Mode

For personal use or internal deployments:

```bash
SERIALMEMORY_MODE=self-hosted
SERIALMEMORY_DEFAULT_TENANT=00000000-0000-0000-0000-000000000000
```

- No JWT authentication required
- All requests use the default tenant
- Full access to all tools

### SaaS Mode

For multi-tenant deployments:

```bash
SERIALMEMORY_MODE=saas
# JWT_SECRET=your-secret-key
# JWT_ISSUER=your-issuer
```

- JWT authentication required
- Tenant ID extracted from JWT claims
- Row-Level Security (RLS) enforces isolation

## SDK Development

### .NET SDK

```bash
cd sdks/dotnet

# Build
dotnet build SerialMemory.Client

# Run example
dotnet run --project examples/PersonalAssistant
```

### Node.js SDK

```bash
cd sdks/node

# Install dependencies
npm install

# Build
npm run build

# Run example
cd examples/personal-assistant
npm install
npm start
```

## Testing

### Run Unit Tests

```bash
dotnet test SerialMemory.Mcp.Tests
```

### Run Integration Tests

```bash
# Ensure database is running
docker compose -f docker-compose.dev.yml up -d postgres

# Run tests
dotnet test SerialMemory.Tests --filter Category=Integration
```

### Test MCP Tools Manually

```bash
# Using Python test script
python test_mcp_tools.py

# Using curl (REST API)
curl -X POST http://localhost:5000/memory/search \
  -H "Content-Type: application/json" \
  -d '{"query": "test query", "limit": 5}'
```

## Troubleshooting

### Database Connection Issues

```bash
# Check Postgres is running
docker ps | grep postgres

# Check logs
docker logs serialmemory-postgres

# Test connection
docker exec -it serialmemory-postgres pg_isready -U postgres
```

### Embedding Model Issues

```bash
# Check Ollama is running
curl http://localhost:11434/api/tags

# Re-pull model
docker exec serialmemory-ollama ollama pull nomic-embed-text

# Check model is loaded
docker exec serialmemory-ollama ollama list
```

### MCP Server Not Responding

```bash
# Check if process is running
ps aux | grep SerialMemory.Mcp

# Check for port conflicts
netstat -an | grep 5432

# Run with verbose logging
SERIALMEMORY_LOG_LEVEL=Debug dotnet run --project SerialMemory.Mcp
```

### Reset Everything

```bash
# Stop and remove all containers and volumes
docker compose -f docker-compose.dev.yml down -v

# Re-run bootstrap
./dev-bootstrap.sh  # or .\dev-bootstrap.ps1 on Windows
```

## Performance Tips

1. **Use an SSD** for Docker volumes (PostgreSQL data)
2. **Allocate 4GB+ RAM** to Docker Desktop
3. **Enable GPU** for Ollama if available (uncomment in docker-compose.dev.yml)
4. **Pre-pull models** before heavy use

## Next Steps

- [SDK Documentation](../sdks/README.md) - Client library usage
- [API Reference](./API_REFERENCE.md) - REST API endpoints
- [Architecture Guide](./ARCHITECTURE.md) - System design
- [Example Projects](../examples/README.md) - Sample applications
