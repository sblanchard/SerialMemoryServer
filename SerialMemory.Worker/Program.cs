using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Telemetry;
using SerialMemory.EventSourcing.Maintenance;
using SerialMemory.EventSourcing.Projections;
using SerialMemory.EventSourcing.Retrieval;
using SerialMemory.EventSourcing.Store;
using SerialMemory.EventSourcing.Streaming;
using SerialMemory.Infrastructure;
using SerialMemory.Infrastructure.Backfill;
using SerialMemory.Infrastructure.Billing;
using SerialMemory.Infrastructure.Classification;
using SerialMemory.Infrastructure.Integrity;
using SerialMemory.Infrastructure.MemoryLayer;
using SerialMemory.Infrastructure.Rag;
using SerialMemory.ML;
using SerialMemory.Worker;
using SerialMemory.Worker.Classification;
using SerialMemory.Worker.Consumers;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

// Connection strings
var pgConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL")
    ?? throw new InvalidOperationException("PostgreSQL connection string is required");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Redis")
    ?? "localhost:6379";

// Create NpgsqlDataSource with pgvector support
var dataSourceBuilder = new NpgsqlDataSourceBuilder(pgConnectionString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);
// Internal database connection factory (for RLS bypass in system operations)
builder.Services.AddSingleton<IInternalDbConnectionFactory>(sp =>
    new InternalDbConnectionFactory(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<InternalDbConnectionFactory>()));
// Register configuration for dependency injection
builder.Services.AddSingleton(_ => new WorkerConfiguration
{
    RedisConnection = redisConnectionString,
    PostgreSqlConnection = pgConnectionString,
    RabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "localhost"
});

// Embedding service
var openAiApiKey = builder.Configuration["OPENAI_API_KEY"]
    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var ollamaBaseUrl = builder.Configuration["OLLAMA_BASE_URL"]
    ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
    ?? "http://localhost:11434";

if (!string.IsNullOrEmpty(openAiApiKey))
{
    var openAiEmbedModel = builder.Configuration["OPENAI_EMBED_MODEL"]
        ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_MODEL")
        ?? "text-embedding-3-small";
    var openAiChatModel = builder.Configuration["OPENAI_MODEL"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
        ?? "gpt-4.1-mini";
    var openAiClient = new OpenAiClient(
        apiKey: openAiApiKey,
        chatModel: openAiChatModel,
        embedModel: openAiEmbedModel,
        embeddingDimension: 1536);
    builder.Services.AddSingleton(openAiClient); // Register OpenAiClient directly for MemorySummarizationService
    builder.Services.AddSingleton<IEmbeddingService>(openAiClient);
    builder.Services.AddSingleton<ILlmService>(openAiClient);
}
else
{
    var ollamaEmbedModel = builder.Configuration["OLLAMA_EMBEDDING_MODEL"]
        ?? Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL")
        ?? "nomic-embed-text";
    builder.Services.AddSingleton<IEmbeddingService>(_ =>
        new OllamaEmbeddingService(ollamaBaseUrl, ollamaEmbedModel));
    builder.Services.AddSingleton<ILlmService>(_ =>
        new OllamaLlmService(ollamaBaseUrl, "qwen2.5:14b-instruct-q4_K_M"));
}

// Event store
builder.Services.AddSingleton<IEventStore>(sp =>
    new PostgresEventStore(
        pgConnectionString,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresEventStore>()));

// Redis event stream publisher
builder.Services.AddSingleton<IEventStreamPublisher>(sp =>
    new RedisEventStreamPublisher(
        redisConnectionString,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<RedisEventStreamPublisher>()));

builder.Services.AddSingleton<IEventStreamSubscriber>(sp =>
    (IEventStreamSubscriber)sp.GetRequiredService<IEventStreamPublisher>());

// Retrieval engine
builder.Services.AddSingleton<IRetrievalEngine>(sp =>
    new CompositeRetrievalEngine(
        pgConnectionString,
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CompositeRetrievalEngine>()));

// Projections
builder.Services.AddSingleton<IProjection>(sp =>
    new MemoryProjection(
        pgConnectionString,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryProjection>()));

// ========================================================================
// EVENT WRITER (for classification pipeline and event sourcing)
// ========================================================================

builder.Services.AddSingleton<IEventWriter>(sp =>
    new EventWriter(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<EventWriter>(),
        sp.GetService<ILiveEventEmitter>())); // Optional - may not be available in worker

// ========================================================================
// CLASSIFICATION SERVICE (L0-L4 pipeline)
// ========================================================================

builder.Services.AddSingleton<IClassificationService>(sp =>
    new ClassificationService(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILlmService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClassificationService>()));

// ========================================================================
// L2 EMBEDDING SERVICE (for RAG pipeline)
// ========================================================================

builder.Services.AddSingleton<IL2EmbeddingService>(sp =>
    new L2EmbeddingService(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<L2EmbeddingService>()));

// ========================================================================
// CLASSIFICATION CONFIG (dwell times for cognitive maturation)
// ========================================================================

builder.Services.AddSingleton(ClassificationConfig.Default);

// ========================================================================
// MEMORY BACKFILL SERVICE (for historical memory processing)
// ========================================================================

builder.Services.AddSingleton<IMemoryBackfillService>(sp =>
    new MemoryBackfillService(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IClassificationService>(),
        sp.GetRequiredService<IEventWriter>(),
        sp.GetRequiredService<IL2EmbeddingService>(),
        sp.GetRequiredService<ClassificationConfig>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryBackfillService>()));

// ========================================================================
// MEMORY LAYER SERVICES (previously unused - now registered)
// ========================================================================

// LayerPromotionService - manages memory layer promotion (L0→L1→L2→L3→L4)
builder.Services.AddSingleton<LayerPromotionService>();

// UsageForecastingService - generates usage forecasts and cost optimization recommendations
builder.Services.AddSingleton<UsageForecastingService>();

// MemorySummarizationService - summarizes related L1 memories into L2 summaries (requires OpenAI)
if (!string.IsNullOrEmpty(openAiApiKey))
{
    builder.Services.AddSingleton<MemorySummarizationService>();
}

// ========================================================================
// BACKGROUND WORKERS
// ========================================================================

// 1. Memory Maintenance Worker - handles decay, archiving, reinforcement
//    Uses batch SQL operations directly (bypasses per-item event sourcing for performance).
builder.Services.AddHostedService<MemoryMaintenanceWorker>(sp =>
    new MemoryMaintenanceWorker(
        pgConnectionString,
        sp.GetRequiredService<IRetrievalEngine>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryMaintenanceWorker>()));

// 2. Projection Host - processes events and updates read models
// Wrapped in a BackgroundService since ProjectionHost doesn't implement IHostedService
builder.Services.AddSingleton<IProjectionHost>(sp =>
    new ProjectionHost(
        sp.GetRequiredService<IEventStore>(),
        sp.GetServices<IProjection>().ToList(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ProjectionHost>()));

builder.Services.AddHostedService<ProjectionHostService>();

// 3. Integrity Worker - computes and verifies content/chain hashes
builder.Services.AddHostedService<IntegrityWorker>(sp =>
    new IntegrityWorker(
        sp,
        pgConnectionString,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<IntegrityWorker>()));

// 4. Memory Classification Worker - L0→L4 classification pipeline with L2 RAG indexing
builder.Services.AddHostedService<MemoryClassificationWorker>(sp =>
    new MemoryClassificationWorker(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IClassificationService>(),
        sp.GetRequiredService<IEventWriter>(),
        sp.GetRequiredService<IL2EmbeddingService>(),
        sp.GetRequiredService<ClassificationConfig>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryClassificationWorker>()));

// 5. Historical Memory Processor - detects and processes all unprocessed historical memories
//    Ensures 100% of memories have L1-L4 layers, runs on startup and every 2 minutes
builder.Services.AddHostedService<HistoricalMemoryProcessor>(sp =>
    new HistoricalMemoryProcessor(
        sp.GetRequiredService<IMemoryBackfillService>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<HistoricalMemoryProcessor>()));

// ========================================================================
// MASSTRANSIT CONFIGURATION
// ========================================================================
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ContextUpdatedConsumer>();
    x.AddConsumer<ContextDeletedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
        var rabbitVHost = builder.Configuration["RabbitMq:VHost"] ?? "/";
        var rabbitUser = builder.Configuration["RabbitMq:User"] ?? "guest";
        var rabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest";

        cfg.Host(rabbitHost, rabbitVHost, h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });

        cfg.UseMessageRetry(r =>
        {
            r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
            r.Handle<Exception>();
        });

        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        cfg.UseRateLimit(1000, TimeSpan.FromSeconds(1));
        cfg.ConfigureEndpoints(context);
    });
});

// Metrics web host for Prometheus scraping
builder.Services.AddHostedService<MetricsWebHostService>();

// OpenTelemetry configuration
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SerialMemory.Worker"))
    .WithMetrics(mb =>
    {
        mb.AddMeter(Metrics.MeterName);
        mb.AddPrometheusExporter();
        mb.AddProcessInstrumentation();
    });

Console.WriteLine("[INFO] SerialMemory.Worker starting...");
Console.WriteLine($"[INFO] PostgreSQL: {(pgConnectionString.Contains("Password") ? "[configured]" : pgConnectionString)}");
Console.WriteLine($"[INFO] Redis: {redisConnectionString}");
Console.WriteLine($"[INFO] Embedding: {(string.IsNullOrEmpty(openAiApiKey) ? "Ollama" : "OpenAI")}");

var host = builder.Build();
await host.RunAsync();
