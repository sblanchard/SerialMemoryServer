FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY ["SerialMemory.Worker/SerialMemory.Worker.csproj", "SerialMemory.Worker/"]
COPY ["SerialMemory.Core/SerialMemory.Core.csproj", "SerialMemory.Core/"]
COPY ["SerialMemory.Contracts/SerialMemory.Contracts.csproj", "SerialMemory.Contracts/"]
COPY ["SerialMemory.Infrastructure/SerialMemory.Infrastructure.csproj", "SerialMemory.Infrastructure/"]
COPY ["SerialMemory.EventSourcing/SerialMemory.EventSourcing.csproj", "SerialMemory.EventSourcing/"]
COPY ["SerialMemory.ML/SerialMemory.ML.csproj", "SerialMemory.ML/"]
RUN dotnet restore "SerialMemory.Worker/SerialMemory.Worker.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/SerialMemory.Worker"
RUN dotnet build "SerialMemory.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SerialMemory.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8081
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SerialMemory.Worker.dll"]
