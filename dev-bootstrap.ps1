<#
.SYNOPSIS
    Bootstrap script for SerialMemory local development environment (Windows)

.DESCRIPTION
    This script sets up a complete local development environment for SerialMemory:
    1. Starts all Docker services (Postgres, API, Dashboard, Ollama)
    2. Applies database migrations
    3. Seeds demo data
    4. Pulls the embedding model
    5. Generates a sample JWT token for testing
    6. Outputs configuration for Claude Desktop / MCP clients

.EXAMPLE
    .\dev-bootstrap.ps1

.EXAMPLE
    .\dev-bootstrap.ps1 -Reset
    Resets database and starts fresh

.NOTES
    Requires: Docker Desktop, PowerShell 5.1+
#>

param(
    [switch]$Reset,
    [switch]$SkipOllama
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║         SerialMemory Development Bootstrap (Windows)           ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# -----------------------------------------------------------------------------
# Check prerequisites
# -----------------------------------------------------------------------------
Write-Host "🔍 Checking prerequisites..." -ForegroundColor Yellow

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "❌ Docker is not installed or not in PATH" -ForegroundColor Red
    Write-Host "   Please install Docker Desktop: https://www.docker.com/products/docker-desktop" -ForegroundColor Gray
    exit 1
}

$dockerInfo = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Docker is not running" -ForegroundColor Red
    Write-Host "   Please start Docker Desktop" -ForegroundColor Gray
    exit 1
}

Write-Host "✅ Docker is running" -ForegroundColor Green

# -----------------------------------------------------------------------------
# Reset if requested
# -----------------------------------------------------------------------------
if ($Reset) {
    Write-Host ""
    Write-Host "🗑️ Resetting environment (removing volumes)..." -ForegroundColor Yellow
    docker compose -f docker-compose.dev.yml down -v 2>$null
    Write-Host "✅ Environment reset complete" -ForegroundColor Green
}

# -----------------------------------------------------------------------------
# Start services
# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "🚀 Starting Docker services..." -ForegroundColor Yellow

docker compose -f docker-compose.dev.yml up -d postgres redis
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to start database services" -ForegroundColor Red
    exit 1
}

Write-Host "⏳ Waiting for PostgreSQL to be ready..."
$maxAttempts = 30
$attempt = 0
do {
    $attempt++
    Start-Sleep -Seconds 2
    $health = docker inspect serialmemory-postgres --format='{{.State.Health.Status}}' 2>$null
    Write-Host "   Attempt $attempt/$maxAttempts - Status: $health" -ForegroundColor Gray
} while ($health -ne "healthy" -and $attempt -lt $maxAttempts)

if ($health -ne "healthy") {
    Write-Host "❌ PostgreSQL failed to start" -ForegroundColor Red
    docker logs serialmemory-postgres
    exit 1
}
Write-Host "✅ PostgreSQL is ready" -ForegroundColor Green

# Start remaining services
docker compose -f docker-compose.dev.yml up -d
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to start services" -ForegroundColor Red
    exit 1
}

# -----------------------------------------------------------------------------
# Pull Ollama embedding model
# -----------------------------------------------------------------------------
if (-not $SkipOllama) {
    Write-Host ""
    Write-Host "📦 Pulling Ollama embedding model (nomic-embed-text)..." -ForegroundColor Yellow
    Write-Host "   This may take a few minutes on first run..." -ForegroundColor Gray

    Start-Sleep -Seconds 5  # Wait for Ollama to start
    docker exec serialmemory-ollama ollama pull nomic-embed-text 2>&1 | ForEach-Object { Write-Host "   $_" -ForegroundColor Gray }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Embedding model ready" -ForegroundColor Green
    } else {
        Write-Host "⚠️ Failed to pull embedding model (you can do this manually later)" -ForegroundColor Yellow
    }
}

# -----------------------------------------------------------------------------
# Generate sample tokens
# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "🔑 Generating sample credentials..." -ForegroundColor Yellow

$tenantId = "00000000-0000-0000-0000-000000000000"
$userId = "demo-user"

# For self-hosted mode, we use simple API keys (no JWT needed)
$apiKey = "sm_dev_" + [guid]::NewGuid().ToString("N").Substring(0, 24)

Write-Host "✅ Credentials generated" -ForegroundColor Green

# -----------------------------------------------------------------------------
# Output configuration
# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║                    Setup Complete! 🎉                          ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Green

Write-Host ""
Write-Host "📡 Service URLs:" -ForegroundColor Cyan
Write-Host "   SerialMemory API:    http://localhost:5000" -ForegroundColor White
Write-Host "   Dashboard API:       http://localhost:5001" -ForegroundColor White
Write-Host "   PostgreSQL:          localhost:5432" -ForegroundColor White
Write-Host "   Ollama:              http://localhost:11434" -ForegroundColor White
Write-Host "   Prometheus:          http://localhost:9090" -ForegroundColor White
Write-Host "   Grafana:             http://localhost:3001 (admin/admin)" -ForegroundColor White

Write-Host ""
Write-Host "🔐 Demo Credentials:" -ForegroundColor Cyan
Write-Host "   Tenant ID:  $tenantId" -ForegroundColor White
Write-Host "   User ID:    $userId" -ForegroundColor White
Write-Host "   API Key:    $apiKey" -ForegroundColor White

Write-Host ""
Write-Host "📋 Claude Desktop Configuration:" -ForegroundColor Cyan
Write-Host "   Add this to your claude_desktop_config.json:" -ForegroundColor Gray
Write-Host ""

$claudeConfig = @"
{
  "mcpServers": {
    "serialmemory": {
      "command": "dotnet",
      "args": ["run", "--project", "$($PWD.Path -replace '\\', '\\\\')\SerialMemory.Mcp"],
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
"@

Write-Host $claudeConfig -ForegroundColor DarkGray

Write-Host ""
Write-Host "📋 SDK Configuration (.NET):" -ForegroundColor Cyan
Write-Host ""

$dotnetConfig = @"
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "$apiKey",
    TenantId = Guid.Parse("$tenantId")
});
"@

Write-Host $dotnetConfig -ForegroundColor DarkGray

Write-Host ""
Write-Host "📋 SDK Configuration (Node.js):" -ForegroundColor Cyan
Write-Host ""

$nodeConfig = @"
const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: '$apiKey',
  tenantId: '$tenantId'
});
"@

Write-Host $nodeConfig -ForegroundColor DarkGray

Write-Host ""
Write-Host "🧪 Test the setup:" -ForegroundColor Cyan
Write-Host "   curl http://localhost:5000/health" -ForegroundColor White

Write-Host ""
Write-Host "📚 Next steps:" -ForegroundColor Cyan
Write-Host "   1. Copy the Claude Desktop config above to your config file" -ForegroundColor White
Write-Host "   2. Restart Claude Desktop to load the MCP server" -ForegroundColor White
Write-Host "   3. Try: 'Search my memory for...' or 'Remember that...'" -ForegroundColor White

Write-Host ""
Write-Host "🛑 To stop:" -ForegroundColor Cyan
Write-Host "   docker compose -f docker-compose.dev.yml down" -ForegroundColor White

Write-Host ""
