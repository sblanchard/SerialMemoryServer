# Self-Hosting Guide

SerialMemory can be self-hosted for development, private deployments, or unlimited usage scenarios.

## Prerequisites

- Docker and Docker Compose
- 4GB+ RAM recommended
- 10GB+ disk space for data and models

## Quick Start

```bash
# Clone the repository
git clone https://github.com/serialmemory/serialmemory.git
cd serialmemory

# Start all services
docker compose up -d

# Run bootstrap script
./scripts/bootstrap-dev.ps1  # Windows
./scripts/bootstrap-dev.sh   # Linux/Mac
```

The bootstrap script:
1. Starts Docker services
2. Pulls the Ollama embedding model
3. Creates a demo tenant and API key
4. Prints example curl commands

## Services

| Service | Port | Description |
|---------|------|-------------|
| PostgreSQL | 5432 | Database with pgvector |
| SerialMemory API | 5000 | REST API endpoints |
| Dashboard API | 5001 | Tenant self-service |
| Ollama | 11434 | Embedding generation |
| Redis | 6379 | Caching & rate limiting |
| Prometheus | 9090 | Metrics collection |
| Grafana | 3001 | Dashboards (admin/admin) |

## Environment Variables

### Required

| Variable | Description | Default |
|----------|-------------|---------|
| `POSTGRES_HOST` | Database host | localhost |
| `POSTGRES_PORT` | Database port | 5432 |
| `POSTGRES_USER` | Database user | postgres |
| `POSTGRES_PASSWORD` | Database password | postgres |
| `POSTGRES_DB` | Database name | contextdb |

### Embeddings

| Variable | Description | Default |
|----------|-------------|---------|
| `OLLAMA_BASE_URL` | Ollama API URL | http://localhost:11434 |
| `OLLAMA_EMBEDDING_MODEL` | Model name | nomic-embed-text |

### Security

| Variable | Description | Default |
|----------|-------------|---------|
| `JWT_SECRET` | JWT signing key | (required in production) |
| `JWT_ISSUER` | JWT issuer | serialmemory |
| `JWT_AUDIENCE` | JWT audience | serialmemory-api |
| `SERIALMEMORY_MODE` | Operating mode | (empty = multi-tenant) |

### Self-Hosted Mode

Set `SERIALMEMORY_MODE=self-hosted` to:
- Bypass JWT authentication
- Disable usage metering
- Remove rate limits
- Use a default tenant

This is suitable for single-user deployments.

## Minimum Viable Secure Deployment

For a secure self-hosted deployment:

### 1. Change Default Passwords

```yaml
# docker-compose.yml
environment:
  POSTGRES_PASSWORD: your-secure-password
```

### 2. Set JWT Secret

```bash
# Generate a secure secret
openssl rand -base64 32
```

```yaml
environment:
  JWT_SECRET: your-generated-secret-here
```

### 3. Enable HTTPS

Use a reverse proxy (nginx, Traefik) with TLS:

```nginx
server {
    listen 443 ssl;
    server_name memory.yourdomain.com;

    ssl_certificate /etc/ssl/certs/your-cert.pem;
    ssl_certificate_key /etc/ssl/private/your-key.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}
```

### 4. Secure Database

- Change default PostgreSQL password
- Restrict network access to Docker internal network
- Enable PostgreSQL SSL for remote connections

### 5. Backup Strategy

```bash
# Backup database
docker exec serialmemory-postgres pg_dump -U postgres contextdb > backup.sql

# Restore
cat backup.sql | docker exec -i serialmemory-postgres psql -U postgres contextdb
```

## Database Schema

The database is initialized with these schemas (in order):

1. `init.sql` - Core tables (memories, entities, relationships)
2. `eventsourcing_schema.sql` - Event store
3. `multi_tenant_schema.sql` - Tenant tables
4. `rls_policies.sql` - Row-level security
5. `usage_metering_schema.sql` - Billing and metering
6. `admin_actions_schema.sql` - Audit logging

## GPU Acceleration

For faster embeddings, enable GPU support in Ollama:

```yaml
# docker-compose.yml
ollama:
  image: ollama/ollama:latest
  deploy:
    resources:
      reservations:
        devices:
          - driver: nvidia
            count: 1
            capabilities: [gpu]
```

Requires:
- NVIDIA GPU
- NVIDIA Container Toolkit installed

## Scaling Considerations

### Horizontal Scaling

- API and Dashboard can be scaled horizontally
- Use Redis for session and cache sharing
- PostgreSQL can use read replicas

### Resource Requirements

| Users | RAM | CPU | Storage |
|-------|-----|-----|---------|
| 1-10 | 4GB | 2 cores | 10GB |
| 10-100 | 8GB | 4 cores | 50GB |
| 100-1000 | 16GB+ | 8+ cores | 200GB+ |

### Performance Tuning

PostgreSQL:
```sql
-- Increase work_mem for complex queries
SET work_mem = '256MB';

-- Tune for vector operations
SET maintenance_work_mem = '1GB';
```

Ollama:
- GPU acceleration reduces embedding time from ~100ms to ~10ms
- Model stays loaded in memory after first use

## Monitoring

### Prometheus Metrics

Access Prometheus at http://localhost:9090

Key metrics:
- `serialmemory_requests_total` - Total API requests
- `serialmemory_request_duration_seconds` - Latency histogram
- `serialmemory_memories_total` - Memory count
- `serialmemory_embeddings_generated` - Embedding operations

### Grafana Dashboards

Access Grafana at http://localhost:3001 (admin/admin)

Pre-built dashboards:
- API Performance
- Memory Statistics
- Tenant Usage

## Troubleshooting

### Services Not Starting

```bash
# Check container status
docker ps -a

# View logs
docker compose logs api
docker compose logs postgres
```

### Database Connection Failed

```bash
# Check PostgreSQL is ready
docker exec serialmemory-postgres pg_isready

# Check connection from API
docker exec serialmemory-api curl -s http://postgres:5432
```

### Embeddings Not Working

```bash
# Check Ollama status
curl http://localhost:11434/api/version

# Pull model if missing
docker exec serialmemory-ollama ollama pull nomic-embed-text

# List available models
docker exec serialmemory-ollama ollama list
```

### Reset Everything

```bash
# Stop and remove all data
docker compose down -v

# Start fresh
docker compose up -d
./scripts/bootstrap-dev.ps1
```

## Upgrading

```bash
# Pull latest images
docker compose pull

# Restart services
docker compose up -d

# Database migrations are applied automatically
```

## Further Reading

- [Overview](./01-overview.md)
- [Claude Desktop Integration](./02-quickstart-claude-mcp.md)
- [Plans and Limits](./05-plans-and-limits.md)
- [Data Lifecycle](./06-data-lifecycle.md)
