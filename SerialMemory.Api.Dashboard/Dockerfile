FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY ["SerialMemory.Api.Dashboard/SerialMemory.Api.Dashboard.csproj", "SerialMemory.Api.Dashboard/"]
COPY ["SerialMemory.Core/SerialMemory.Core.csproj", "SerialMemory.Core/"]
COPY ["SerialMemory.Infrastructure/SerialMemory.Infrastructure.csproj", "SerialMemory.Infrastructure/"]
RUN dotnet restore "SerialMemory.Api.Dashboard/SerialMemory.Api.Dashboard.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/SerialMemory.Api.Dashboard"
RUN dotnet build "SerialMemory.Api.Dashboard.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SerialMemory.Api.Dashboard.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SerialMemory.Api.Dashboard.dll"]
