# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files
COPY SerialMemoryServer.sln .
COPY SerialMemory.Core/SerialMemory.Core.csproj SerialMemory.Core/
COPY SerialMemory.Infrastructure/SerialMemory.Infrastructure.csproj SerialMemory.Infrastructure/
COPY SerialMemory.ML/SerialMemory.ML.csproj SerialMemory.ML/
COPY SerialMemory.Contracts/SerialMemory.Contracts.csproj SerialMemory.Contracts/
COPY SerialMemory.EventSourcing/SerialMemory.EventSourcing.csproj SerialMemory.EventSourcing/
COPY SerialMemory.Mcp/SerialMemory.Mcp.csproj SerialMemory.Mcp/

# Restore dependencies
RUN dotnet restore SerialMemory.Mcp/SerialMemory.Mcp.csproj

# Copy source code
COPY SerialMemory.Core/ SerialMemory.Core/
COPY SerialMemory.Infrastructure/ SerialMemory.Infrastructure/
COPY SerialMemory.ML/ SerialMemory.ML/
COPY SerialMemory.Contracts/ SerialMemory.Contracts/
COPY SerialMemory.EventSourcing/ SerialMemory.EventSourcing/
COPY SerialMemory.Mcp/ SerialMemory.Mcp/

# Build and publish
RUN dotnet publish SerialMemory.Mcp/SerialMemory.Mcp.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Set environment variables
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_ENVIRONMENT=Production

# The MCP server uses STDIO, no ports needed
# Use ENTRYPOINT for consistent behavior
ENTRYPOINT ["dotnet", "SerialMemory.Mcp.dll"]
