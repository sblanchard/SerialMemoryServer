# SerialMemory MCP Client - Docker-first client for Claude Desktop
# This proxies safe MCP operations to a remote SerialMemory backend.

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy just the project file first to leverage Docker cache
COPY SerialMemory.McpClient.csproj ./
RUN dotnet restore

# Copy all source files
COPY . .

# Build and publish
RUN dotnet publish -c Release -o /app --no-restore

# Runtime image - minimal footprint
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview AS runtime
WORKDIR /app

# Security: run as non-root user
RUN groupadd -r mcpuser && useradd -r -g mcpuser mcpuser
USER mcpuser

COPY --from=build /app .

# Environment variables (to be overridden at runtime)
ENV MCP_SERVER_URL="https://serialmemory.serialcoder.com"
ENV MCP_API_KEY=""
ENV MCP_MODE="developer"

# Healthcheck is not applicable for STDIO-based MCP
# The process must run interactively

ENTRYPOINT ["dotnet", "SerialMemory.McpClient.dll"]
