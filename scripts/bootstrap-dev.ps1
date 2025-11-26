<#
.SYNOPSIS
    Bootstrap script for SerialMemory local development environment.

.DESCRIPTION
    - Starts Docker services (if not running)
    - Pulls Ollama embedding model
    - Creates demo tenant and API key
    - Prints example curl commands

.EXAMPLE
    .\scripts\bootstrap-dev.ps1
#>

$ErrorActionPreference = "Stop"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " SerialMemory Development Bootstrap" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$DashboardUrl = "http://localhost:5001"
$ApiUrl = "http://localhost:5000"
$PostgresHost = "localhost"
$PostgresPort = 5432
$PostgresUser = "postgres"
$PostgresPassword = "postgres"
$PostgresDb = "contextdb"

# Check Docker
Write-Host "[1/5] Checking Docker..." -ForegroundColor Yellow
try {
    docker info | Out-Null
    Write-Host "      Docker is running." -ForegroundColor Green
} catch {
    Write-Host "      ERROR: Docker is not running. Please start Docker Desktop." -ForegroundColor Red
    exit 1
}

# Start services
Write-Host "[2/5] Starting Docker services..." -ForegroundColor Yellow
docker compose up -d
Start-Sleep -Seconds 5

# Wait for PostgreSQL
Write-Host "[3/5] Waiting for PostgreSQL to be ready..." -ForegroundColor Yellow
$maxAttempts = 30
$attempt = 0
do {
    $attempt++
    try {
        docker exec serialmemory-postgres pg_isready -U postgres -d contextdb | Out-Null
        Write-Host "      PostgreSQL is ready." -ForegroundColor Green
        break
    } catch {
        if ($attempt -ge $maxAttempts) {
            Write-Host "      ERROR: PostgreSQL did not become ready in time." -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Seconds 2
    }
} while ($true)

# Pull Ollama model
Write-Host "[4/5] Pulling Ollama embedding model (nomic-embed-text)..." -ForegroundColor Yellow
docker exec serialmemory-ollama ollama pull nomic-embed-text 2>&1 | Out-Null
Write-Host "      Model pulled successfully." -ForegroundColor Green

# Wait for Dashboard API
Write-Host "[5/5] Waiting for Dashboard API..." -ForegroundColor Yellow
$maxAttempts = 30
$attempt = 0
do {
    $attempt++
    try {
        $response = Invoke-RestMethod -Uri "$DashboardUrl/health" -Method Get -TimeoutSec 2
        Write-Host "      Dashboard API is ready." -ForegroundColor Green
        break
    } catch {
        if ($attempt -ge $maxAttempts) {
            Write-Host "      WARNING: Dashboard API not responding. Continuing anyway..." -ForegroundColor Yellow
            break
        }
        Start-Sleep -Seconds 2
    }
} while ($true)

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " Creating Demo Tenant" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

# Create demo tenant via signup endpoint
$signupBody = @{
    tenantName = "Demo Tenant"
    slug = "demo"
    email = "demo@example.com"
    plan = "starter"
} | ConvertTo-Json

try {
    $signupResponse = Invoke-RestMethod -Uri "$DashboardUrl/signup" -Method Post -Body $signupBody -ContentType "application/json"

    $tenantId = $signupResponse.tenantId
    $apiKey = $signupResponse.apiKey.key
    $token = $signupResponse.token

    Write-Host ""
    Write-Host "Demo tenant created successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Tenant ID:   $tenantId" -ForegroundColor White
    Write-Host "  Tenant Slug: demo" -ForegroundColor White
    Write-Host "  Email:       demo@example.com" -ForegroundColor White
    Write-Host ""
    Write-Host "  API Key:     " -NoNewline -ForegroundColor White
    Write-Host "$apiKey" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  JWT Token:   $($token.Substring(0, 50))..." -ForegroundColor Gray

} catch {
    # Check if tenant already exists
    if ($_.Exception.Response.StatusCode -eq 409) {
        Write-Host ""
        Write-Host "Demo tenant already exists. Using existing credentials." -ForegroundColor Yellow
        Write-Host "To reset, run: docker compose down -v && docker compose up -d" -ForegroundColor Gray

        # For existing tenant, we can't retrieve the API key (it's hashed)
        # Show instructions for creating a new key
        Write-Host ""
        Write-Host "To create a new API key, authenticate with JWT and call POST /api-keys" -ForegroundColor Gray
    } else {
        Write-Host "WARNING: Could not create demo tenant: $_" -ForegroundColor Yellow
        Write-Host "You may need to wait for services to fully start and re-run this script." -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " Example Commands" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "# Sign up a new tenant:" -ForegroundColor Gray
Write-Host @"
curl -X POST "$DashboardUrl/signup" \
  -H "Content-Type: application/json" \
  -d '{"tenantName":"My Company","email":"admin@mycompany.com"}'
"@ -ForegroundColor White
Write-Host ""

Write-Host "# Check tenant usage (with API key):" -ForegroundColor Gray
Write-Host @"
curl "$DashboardUrl/tenant/usage" \
  -H "X-API-Key: sm_live_YOUR_API_KEY"
"@ -ForegroundColor White
Write-Host ""

Write-Host "# Get tenant limits:" -ForegroundColor Gray
Write-Host @"
curl "$DashboardUrl/tenant/limits" \
  -H "X-API-Key: sm_live_YOUR_API_KEY"
"@ -ForegroundColor White
Write-Host ""

Write-Host "# Ingest a memory (via API):" -ForegroundColor Gray
Write-Host @"
curl -X POST "$ApiUrl/tools/memory_ingest" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: sm_live_YOUR_API_KEY" \
  -d '{"content":"John works at Acme Corp as an engineer."}'
"@ -ForegroundColor White
Write-Host ""

Write-Host "# Search memories:" -ForegroundColor Gray
Write-Host @"
curl -X POST "$ApiUrl/tools/memory_search" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: sm_live_YOUR_API_KEY" \
  -d '{"query":"Who works at Acme?"}'
"@ -ForegroundColor White
Write-Host ""

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " Services Running" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  SerialMemory API:     $ApiUrl" -ForegroundColor White
Write-Host "  Dashboard API:        $DashboardUrl" -ForegroundColor White
Write-Host "  PostgreSQL:           $PostgresHost`:$PostgresPort" -ForegroundColor White
Write-Host "  Ollama:               http://localhost:11434" -ForegroundColor White
Write-Host "  Prometheus:           http://localhost:9090" -ForegroundColor White
Write-Host "  Grafana:              http://localhost:3001 (admin/admin)" -ForegroundColor White
Write-Host ""
Write-Host "To stop: docker compose down" -ForegroundColor Gray
Write-Host "To reset: docker compose down -v && docker compose up -d" -ForegroundColor Gray
Write-Host ""
