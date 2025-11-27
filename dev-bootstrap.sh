#!/bin/bash
# =============================================================================
# SerialMemory Development Bootstrap (Linux/macOS)
# =============================================================================
# This script sets up a complete local development environment for SerialMemory:
# 1. Starts all Docker services (Postgres, API, Dashboard, Ollama)
# 2. Applies database migrations
# 3. Seeds demo data
# 4. Pulls the embedding model
# 5. Generates a sample API key for testing
# 6. Outputs configuration for Claude Desktop / MCP clients
#
# Usage:
#   ./dev-bootstrap.sh           # Normal start
#   ./dev-bootstrap.sh --reset   # Reset database and start fresh
#   ./dev-bootstrap.sh --skip-ollama  # Skip embedding model download
# =============================================================================

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
NC='\033[0m' # No Color

# Parse arguments
RESET=false
SKIP_OLLAMA=false
for arg in "$@"; do
    case $arg in
        --reset)
            RESET=true
            ;;
        --skip-ollama)
            SKIP_OLLAMA=true
            ;;
    esac
done

echo ""
echo -e "${CYAN}╔════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║       SerialMemory Development Bootstrap (Linux/macOS)         ║${NC}"
echo -e "${CYAN}╚════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# -----------------------------------------------------------------------------
# Check prerequisites
# -----------------------------------------------------------------------------
echo -e "${YELLOW}🔍 Checking prerequisites...${NC}"

if ! command -v docker &> /dev/null; then
    echo -e "${RED}❌ Docker is not installed or not in PATH${NC}"
    echo -e "${GRAY}   Please install Docker: https://docs.docker.com/get-docker/${NC}"
    exit 1
fi

if ! docker info &> /dev/null; then
    echo -e "${RED}❌ Docker is not running${NC}"
    echo -e "${GRAY}   Please start Docker Desktop or the Docker daemon${NC}"
    exit 1
fi

echo -e "${GREEN}✅ Docker is running${NC}"

# -----------------------------------------------------------------------------
# Reset if requested
# -----------------------------------------------------------------------------
if [ "$RESET" = true ]; then
    echo ""
    echo -e "${YELLOW}🗑️ Resetting environment (removing volumes)...${NC}"
    docker compose -f docker-compose.dev.yml down -v 2>/dev/null || true
    echo -e "${GREEN}✅ Environment reset complete${NC}"
fi

# -----------------------------------------------------------------------------
# Start services
# -----------------------------------------------------------------------------
echo ""
echo -e "${YELLOW}🚀 Starting Docker services...${NC}"

docker compose -f docker-compose.dev.yml up -d postgres redis
if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Failed to start database services${NC}"
    exit 1
fi

echo "⏳ Waiting for PostgreSQL to be ready..."
max_attempts=30
attempt=0
while [ $attempt -lt $max_attempts ]; do
    attempt=$((attempt + 1))
    health=$(docker inspect serialmemory-postgres --format='{{.State.Health.Status}}' 2>/dev/null || echo "unknown")
    echo -e "${GRAY}   Attempt $attempt/$max_attempts - Status: $health${NC}"

    if [ "$health" = "healthy" ]; then
        break
    fi
    sleep 2
done

if [ "$health" != "healthy" ]; then
    echo -e "${RED}❌ PostgreSQL failed to start${NC}"
    docker logs serialmemory-postgres
    exit 1
fi
echo -e "${GREEN}✅ PostgreSQL is ready${NC}"

# Start remaining services
docker compose -f docker-compose.dev.yml up -d
if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Failed to start services${NC}"
    exit 1
fi

# -----------------------------------------------------------------------------
# Pull Ollama embedding model
# -----------------------------------------------------------------------------
if [ "$SKIP_OLLAMA" = false ]; then
    echo ""
    echo -e "${YELLOW}📦 Pulling Ollama embedding model (nomic-embed-text)...${NC}"
    echo -e "${GRAY}   This may take a few minutes on first run...${NC}"

    sleep 5  # Wait for Ollama to start
    docker exec serialmemory-ollama ollama pull nomic-embed-text 2>&1 | while read line; do
        echo -e "${GRAY}   $line${NC}"
    done

    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✅ Embedding model ready${NC}"
    else
        echo -e "${YELLOW}⚠️ Failed to pull embedding model (you can do this manually later)${NC}"
    fi
fi

# -----------------------------------------------------------------------------
# Generate sample tokens
# -----------------------------------------------------------------------------
echo ""
echo -e "${YELLOW}🔑 Generating sample credentials...${NC}"

TENANT_ID="00000000-0000-0000-0000-000000000000"
USER_ID="demo-user"

# For self-hosted mode, we use simple API keys (no JWT needed)
API_KEY="sm_dev_$(head -c 16 /dev/urandom | xxd -p | head -c 24)"

echo -e "${GREEN}✅ Credentials generated${NC}"

# -----------------------------------------------------------------------------
# Output configuration
# -----------------------------------------------------------------------------
echo ""
echo -e "${GREEN}╔════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║                    Setup Complete! 🎉                          ║${NC}"
echo -e "${GREEN}╚════════════════════════════════════════════════════════════════╝${NC}"

echo ""
echo -e "${CYAN}📡 Service URLs:${NC}"
echo "   SerialMemory API:    http://localhost:5000"
echo "   Dashboard API:       http://localhost:5001"
echo "   PostgreSQL:          localhost:5432"
echo "   Ollama:              http://localhost:11434"
echo "   Prometheus:          http://localhost:9090"
echo "   Grafana:             http://localhost:3001 (admin/admin)"

echo ""
echo -e "${CYAN}🔐 Demo Credentials:${NC}"
echo "   Tenant ID:  $TENANT_ID"
echo "   User ID:    $USER_ID"
echo "   API Key:    $API_KEY"

echo ""
echo -e "${CYAN}📋 Claude Desktop Configuration:${NC}"
echo -e "${GRAY}   Add this to your claude_desktop_config.json:${NC}"
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cat << EOF
{
  "mcpServers": {
    "serialmemory": {
      "command": "dotnet",
      "args": ["run", "--project", "${SCRIPT_DIR}/SerialMemory.Mcp"],
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
EOF

echo ""
echo -e "${CYAN}📋 SDK Configuration (.NET):${NC}"
echo ""
cat << EOF
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "$API_KEY",
    TenantId = Guid.Parse("$TENANT_ID")
});
EOF

echo ""
echo -e "${CYAN}📋 SDK Configuration (Node.js):${NC}"
echo ""
cat << EOF
const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: '$API_KEY',
  tenantId: '$TENANT_ID'
});
EOF

echo ""
echo -e "${CYAN}🧪 Test the setup:${NC}"
echo "   curl http://localhost:5000/health"

echo ""
echo -e "${CYAN}📚 Next steps:${NC}"
echo "   1. Copy the Claude Desktop config above to your config file"
echo "   2. Restart Claude Desktop to load the MCP server"
echo "   3. Try: 'Search my memory for...' or 'Remember that...'"

echo ""
echo -e "${CYAN}🛑 To stop:${NC}"
echo "   docker compose -f docker-compose.dev.yml down"

echo ""
