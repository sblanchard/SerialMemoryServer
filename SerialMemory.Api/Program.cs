using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using SerialMemory.ML;
using StackExchange.Redis;
using Microsoft.AspNetCore.SignalR;
using SerialMemory.Api.Realtime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SerialMemory.Core.Telemetry;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS for web frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Redis connection (optional - for context store)
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
try
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.AddSingleton<IContextStore, RedisContextStore>();
}
catch
{
    // Redis not available - skip context store features
    builder.Services.AddSingleton<IContextStore, InMemoryContextStore>();
}

// Knowledge Graph Services (PostgreSQL)
var pgConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? $"Host={Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost"};" +
       $"Port={Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432"};" +
       $"Database={Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb"};" +
       $"Username={Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres"};" +
       $"Password={Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres"}";

var embeddingServiceUrl = builder.Configuration["EmbeddingServiceUrl"]
    ?? Environment.GetEnvironmentVariable("EMBEDDING_SERVICE_URL")
    ?? "http://localhost:8765";

builder.Services.AddSingleton<IKnowledgeGraphStore>(_ => new PostgresKnowledgeGraphStore(pgConnectionString));
builder.Services.AddSingleton<IEmbeddingService>(_ => new HttpEmbeddingService(embeddingServiceUrl));
builder.Services.AddSingleton<IEntityExtractionService, PatternEntityExtractionService>();
builder.Services.AddSingleton<KnowledgeGraphService>();

// MassTransit Configuration (optional - for event publishing)
try
{
    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });
            cfg.ConfigureEndpoints(context);
            cfg.UseMessageRetry(r => r.Exponential(5,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5)));
        });
    });
    builder.Services.AddScoped<MassTransitEventPublisher>();
}
catch
{
    // RabbitMQ not available - skip event publishing
}

// SignalR (optional - for real-time updates)
try
{
    builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
        o.MaximumReceiveMessageSize = 64 * 1024;
    });
}
catch
{
    // SignalR setup failed
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SerialMemory.Api"))
    .WithMetrics(mb =>
    {
        mb.AddMeter(Metrics.MeterName);
        mb.AddPrometheusExporter();
        mb.AddRuntimeInstrumentation();
        mb.AddProcessInstrumentation();
        mb.AddHttpClientInstrumentation();
        mb.AddAspNetCoreInstrumentation();
    })
    .WithTracing(tb =>
    {
        tb.AddAspNetCoreInstrumentation();
        tb.AddHttpClientInstrumentation();
    });

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI();

app.MapPrometheusScrapingEndpoint();

// Try to map SignalR hub if available
try
{
    app.MapHub<ContextHub>("/hub/context");
}
catch
{
    // SignalR not configured
}

// ============================================
// KNOWLEDGE GRAPH API ENDPOINTS
// ============================================

// Search memories
app.MapGet("/api/memories/search", async (
    string query,
    string? mode,
    int? limit,
    float? threshold,
    KnowledgeGraphService kgService) =>
{
    var searchMode = mode?.ToLower() switch
    {
        "semantic" => SearchMode.Semantic,
        "text" => SearchMode.Text,
        _ => SearchMode.Hybrid
    };

    var results = await kgService.SearchMemoriesAsync(
        query,
        searchMode,
        limit ?? 10,
        threshold ?? 0.5f,
        includeEntities: true);

    return Results.Ok(results);
});

// Get recent memories
app.MapGet("/api/memories/recent", async (int? limit, KnowledgeGraphService kgService) =>
{
    var results = await kgService.GetRecentMemoriesAsync(limit ?? 20, includeEntities: true);
    return Results.Ok(results);
});

// Ingest a new memory
app.MapPost("/api/memories", async (MemoryIngestRequest request, KnowledgeGraphService kgService) =>
{
    var result = await kgService.IngestMemoryAsync(
        request.Content,
        request.Source,
        request.SessionId,
        request.Metadata,
        request.ExtractEntities ?? true);

    return Results.Created($"/api/memories/{result.MemoryId}", result);
});

// Get knowledge graph data for visualization
app.MapGet("/api/graph", async (
    string? query,
    int? hops,
    int? limit,
    KnowledgeGraphService kgService,
    IKnowledgeGraphStore store) =>
{
    if (!string.IsNullOrEmpty(query))
    {
        // Multi-hop search from a query
        var result = await kgService.MultiHopSearchAsync(query, hops ?? 2, limit ?? 10);

        return Results.Ok(new
        {
            nodes = result.Entities.Select(e => new
            {
                id = e.Id.ToString(),
                label = e.Name,
                group = e.Type,
                title = $"{e.Type}: {e.Name}"
            }),
            edges = result.Relationships.Select(r => new
            {
                from = r.SourceId.ToString(),
                to = r.TargetId.ToString(),
                label = r.Type,
                title = $"{r.Source} → {r.Target}: {r.Type} (confidence: {r.Confidence:P0})"
            }),
            memories = result.Memories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                createdAt = m.CreatedAt,
                entities = m.Entities.Select(e => e.Name)
            })
        });
    }
    else
    {
        // Get recent memories and build graph from their entities
        var recentMemories = await kgService.GetRecentMemoriesAsync(limit ?? 50, includeEntities: true);

        var entityMap = new Dictionary<string, EntityInfo>();
        var relationships = new List<object>();

        foreach (var memory in recentMemories)
        {
            foreach (var entity in memory.Entities)
            {
                var key = $"{entity.Type}:{entity.Name}";
                if (!entityMap.ContainsKey(key))
                {
                    entityMap[key] = entity;
                }
            }

            // Create edges between entities in the same memory
            for (int i = 0; i < memory.Entities.Count; i++)
            {
                for (int j = i + 1; j < memory.Entities.Count; j++)
                {
                    relationships.Add(new
                    {
                        from = memory.Entities[i].Id.ToString(),
                        to = memory.Entities[j].Id.ToString(),
                        label = "co-occurs",
                        dashes = true
                    });
                }
            }
        }

        return Results.Ok(new
        {
            nodes = entityMap.Values.Select(e => new
            {
                id = e.Id.ToString(),
                label = e.Name,
                group = e.Type,
                title = $"{e.Type}: {e.Name}"
            }),
            edges = relationships,
            memories = recentMemories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                createdAt = m.CreatedAt,
                entities = m.Entities.Select(e => e.Name)
            })
        });
    }
});

// Get user persona
app.MapGet("/api/persona", async (string? userId, KnowledgeGraphService kgService) =>
{
    var persona = await kgService.GetUserPersonaAsync(userId ?? "default_user");
    return Results.Ok(persona);
});

// Set user persona attribute
app.MapPost("/api/persona", async (UserPersonaRequest request, KnowledgeGraphService kgService) =>
{
    await kgService.SetUserPersonaAttributeAsync(
        request.AttributeType,
        request.AttributeKey,
        request.AttributeValue,
        request.Confidence ?? 1.0f,
        request.UserId ?? "default_user");

    return Results.Ok();
});

// Session management
app.MapPost("/api/sessions", async (SessionRequest? request, KnowledgeGraphService kgService) =>
{
    var sessionId = await kgService.CreateConversationSessionAsync(
        request?.SessionName,
        request?.ClientType);

    return Results.Created($"/api/sessions/{sessionId}", new { sessionId });
});

app.MapGet("/api/sessions/recent", async (int? limit, KnowledgeGraphService kgService) =>
{
    var sessions = await kgService.GetRecentSessionsAsync(limit ?? 10);
    return Results.Ok(sessions);
});

app.MapPost("/api/sessions/{sessionId}/end", async (Guid sessionId, KnowledgeGraphService kgService) =>
{
    await kgService.EndConversationSessionAsync(sessionId);
    return Results.Ok();
});

// CORE Import
app.MapPost("/api/import/core", async (CoreExportData coreData, KnowledgeGraphService kgService) =>
{
    var result = await kgService.ImportFromCoreAsync(coreData);
    return Results.Ok(result);
});

// ============================================
// LEGACY CONTEXT ENDPOINTS (Redis-based)
// ============================================

app.MapGet("/context", async (IContextStore store) =>
{
    var keys = await store.ListKeysAsync();
    return Results.Ok(keys);
});

app.MapGet("/context/{key}", async (string key, IContextStore store) =>
{
    var value = await store.GetAsync(key);
    return value is null ? Results.NotFound() : Results.Ok(value);
});

app.MapPost("/context/{key}", async (string key, HttpRequest req, IContextStore store) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    await store.SetAsync(key, body);
    return Results.Ok();
});

app.MapDelete("/context/{key}", async (string key, IContextStore store) =>
{
    await store.DeleteAsync(key);
    return Results.Ok();
});

app.Run();

// Request DTOs
record MemoryIngestRequest(
    string Content,
    string? Source = null,
    Guid? SessionId = null,
    Dictionary<string, object>? Metadata = null,
    bool? ExtractEntities = true);

record UserPersonaRequest(
    string AttributeType,
    string AttributeKey,
    string AttributeValue,
    float? Confidence = 1.0f,
    string? UserId = null);

record SessionRequest(
    string? SessionName = null,
    string? ClientType = null);

// Simple in-memory context store fallback
class InMemoryContextStore : IContextStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_store.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<string>> ListKeysAsync() =>
        Task.FromResult<IEnumerable<string>>(_store.Keys);
}
