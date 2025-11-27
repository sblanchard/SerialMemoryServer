#!/bin/bash
#
# Bootstrap script for SerialMemory local development environment.
#
# - Starts Docker services (if not running)
# - Pulls Ollama embedding model
# - Creates demo tenant and API key
# - Prints example curl commands
#
# Usage: ./scripts/bootstrap-dev.sh

set -e

CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
WHITE='\033[1;37m'
GRAY='\033[0;37m'
NC='\033[0m' # No Color

# Configuration
DASHBOARD_URL="http://localhost:5001"
API_URL="http://localhost:5000"
POSTGRES_HOST="localhost"
POSTGRES_PORT="5432"

echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN} SerialMemory Development Bootstrap${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""

# Check Docker
echo -e "${YELLOW}[1/5] Checking Docker...${NC}"
if ! docker info > /dev/null 2>&1; then
    echo -e "${RED}      ERROR: Docker is not running. Please start Docker.${NC}"
    exit 1
fi
echo -e "${GREEN}      Docker is running.${NC}"

# Start services
echo -e "${YELLOW}[2/5] Starting Docker services...${NC}"
docker compose up -d
sleep 5

# Wait for PostgreSQL
echo -e "${YELLOW}[3/5] Waiting for PostgreSQL to be ready...${NC}"
MAX_ATTEMPTS=30
ATTEMPT=0
until docker exec serialmemory-postgres pg_isready -U postgres -d contextdb > /dev/null 2>&1; do
    ATTEMPT=$((ATTEMPT + 1))
    if [ $ATTEMPT -ge $MAX_ATTEMPTS ]; then
        echo -e "${RED}      ERROR: PostgreSQL did not become ready in time.${NC}"
        exit 1
    fi
    sleep 2
done
echo -e "${GREEN}      PostgreSQL is ready.${NC}"

# Pull Ollama model
echo -e "${YELLOW}[4/5] Pulling Ollama embedding model (nomic-embed-text)...${NC}"
docker exec serialmemory-ollama ollama pull nomic-embed-text > /dev/null 2>&1
echo -e "${GREEN}      Model pulled successfully.${NC}"

# Wait for Dashboard API
echo -e "${YELLOW}[5/5] Waiting for Dashboard API...${NC}"
MAX_ATTEMPTS=30
ATTEMPT=0
until curl -s "$DASHBOARD_URL/health" > /dev/null 2>&1; do
    ATTEMPT=$((ATTEMPT + 1))
    if [ $ATTEMPT -ge $MAX_ATTEMPTS ]; then
        echo -e "${YELLOW}      WARNING: Dashboard API not responding. Continuing anyway...${NC}"
        break
    fi
    sleep 2
done
echo -e "${GREEN}      Dashboard API is ready.${NC}"

echo ""
echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN} Creating Demo Tenant${NC}"
echo -e "${CYAN}======================================${NC}"

# Create demo tenant via signup endpoint
SIGNUP_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$DASHBOARD_URL/signup" \
    -H "Content-Type: application/json" \
    -d '{"tenantName":"Demo Tenant","slug":"demo","email":"demo@example.com","plan":"starter"}')

HTTP_CODE=$(echo "$SIGNUP_RESPONSE" | tail -n1)
BODY=$(echo "$SIGNUP_RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "201" ]; then
    TENANT_ID=$(echo "$BODY" | grep -o '"tenantId":"[^"]*"' | cut -d'"' -f4)
    API_KEY=$(echo "$BODY" | grep -o '"key":"[^"]*"' | cut -d'"' -f4)

    echo ""
    echo -e "${GREEN}Demo tenant created successfully!${NC}"
    echo ""
    echo -e "  ${WHITE}Tenant ID:   $TENANT_ID${NC}"
    echo -e "  ${WHITE}Tenant Slug: demo${NC}"
    echo -e "  ${WHITE}Email:       demo@example.com${NC}"
    echo ""
    echo -e "  ${WHITE}API Key:     ${YELLOW}$API_KEY${NC}"
    echo ""
elif [ "$HTTP_CODE" = "409" ]; then
    echo ""
    echo -e "${YELLOW}Demo tenant already exists. Using existing credentials.${NC}"
    echo -e "${GRAY}To reset, run: docker compose down -v && docker compose up -d${NC}"
else
    echo ""
    echo -e "${YELLOW}WARNING: Could not create demo tenant (HTTP $HTTP_CODE)${NC}"
    echo -e "${GRAY}You may need to wait for services to fully start and re-run this script.${NC}"
fi

echo ""
echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN} Example Commands${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""

echo -e "${GRAY}# Sign up a new tenant:${NC}"
cat << 'EOF'
curl -X POST "http://localhost:5001/signup" \
  -H "Content-Type: application/json" \
  -d '{"tenantName":"My Company","email":"admin@mycompany.com"}'
EOF
echo ""

echo -e "${GRAY}# Check tenant usage (with API key):${NC}"
cat << 'EOF'
curl "http://localhost:5001/tenant/usage" \
  -H "X-API-Key: sm_live_YOUR_API_KEY"
EOF
echo ""

echo -e "${GRAY}# Get tenant limits:${NC}"
cat << 'EOF'
curl "http://localhost:5001/tenant/limits" \
  -H "X-API-Key: sm_live_YOUR_API_KEY"
EOF
echo ""

echo -e "${GRAY}# Ingest a memory (via API):${NC}"
cat << 'EOF'
curl -X POST "http://localhost:5000/tools/memory_ingest" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: sm_live_YOUR_API_KEY" \
  -d '{"content":"John works at Acme Corp as an engineer."}'
EOF
echo ""

echo -e "${GRAY}# Search memories:${NC}"
cat << 'EOF'
curl -X POST "http://localhost:5000/tools/memory_search" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: sm_live_YOUR_API_KEY" \
  -d '{"query":"Who works at Acme?"}'
EOF
echo ""

echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN} Services Running${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""
echo -e "  ${WHITE}SerialMemory API:     http://localhost:5000${NC}"
echo -e "  ${WHITE}Dashboard API:        http://localhost:5001${NC}"
echo -e "  ${WHITE}PostgreSQL:           localhost:5432${NC}"
echo -e "  ${WHITE}Ollama:               http://localhost:11434${NC}"
echo -e "  ${WHITE}Prometheus:           http://localhost:9090${NC}"
echo -e "  ${WHITE}Grafana:              http://localhost:3001 (admin/admin)${NC}"
echo ""
echo -e "${GRAY}To stop: docker compose down${NC}"
echo -e "${GRAY}To reset: docker compose down -v && docker compose up -d${NC}"
echo ""
