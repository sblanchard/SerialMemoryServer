# SerialMemory MCP Client - Zero-parameter Docker startup
# Interactive setup on first run, persistent config in /data volume.

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project file and restore
COPY SerialMemory.McpClient.csproj ./
RUN dotnet restore

# Copy source and build
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview AS runtime
WORKDIR /app

# Security: run as non-root user
RUN groupadd -r mcpuser && useradd -r -g mcpuser mcpuser

# Create data directory with correct permissions
RUN mkdir -p /data && chown mcpuser:mcpuser /data

# Declare volume for persistent config
VOLUME ["/data"]

# Switch to non-root user
USER mcpuser

# Copy built application
COPY --from=build /app .

# No environment variables required - interactive setup on first run
# Run with: docker run -it -v serialmemory_data:/data serialcoder/serialmemory-mcp

ENTRYPOINT ["dotnet", "SerialMemory.McpClient.dll"]
