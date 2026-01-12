# SerialMemory MCP Bridge
# Dual-mode: Local TCP server on 4317 → WebSocket bridge to SerialMemory SaaS
# Interactive setup on first run, persistent config in /data volume

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore
COPY SerialMemory.McpClient.csproj ./
RUN dotnet restore -r linux-x64

# Copy source and build (restore again since COPY overwrites obj folder)
COPY . .
RUN dotnet publish -c Release -r linux-x64 -o /app

# Runtime stage - minimal image
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

# Security: run as non-root user
RUN groupadd -r mcpuser && useradd -r -g mcpuser mcpuser

# Create data directory with correct permissions
RUN mkdir -p /data && chown mcpuser:mcpuser /data

# Copy built application
COPY --from=build /app .

# Switch to non-root user
USER mcpuser

# Expose MCP port
EXPOSE 4317

# Declare volume for persistent config
VOLUME ["/data"]

# Run the self-contained binary
ENTRYPOINT ["./SerialMemory.McpClient"]
