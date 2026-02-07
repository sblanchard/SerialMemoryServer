FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY ["SerialMemory.Api/SerialMemory.Api.csproj", "SerialMemory.Api/"]
COPY ["SerialMemory.Core/SerialMemory.Core.csproj", "SerialMemory.Core/"]
COPY ["SerialMemory.Infrastructure/SerialMemory.Infrastructure.csproj", "SerialMemory.Infrastructure/"]
COPY ["SerialMemory.ML/SerialMemory.ML.csproj", "SerialMemory.ML/"]
RUN dotnet restore "SerialMemory.Api/SerialMemory.Api.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/SerialMemory.Api"
RUN dotnet build "SerialMemory.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SerialMemory.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SerialMemory.Api.dll"]
