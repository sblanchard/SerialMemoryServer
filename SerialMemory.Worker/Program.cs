using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SerialMemory.Core.Telemetry;
using SerialMemory.Worker;
using SerialMemory.Worker.Consumers;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

// Register configuration for dependency injection
builder.Services.AddSingleton(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    return new WorkerConfiguration
    {
        RedisConnection = configuration.GetConnectionString("Redis")!,
        PostgreSqlConnection = configuration.GetConnectionString("PostgreSql")!,
        RabbitMqHost = configuration["RabbitMq:Host"]!
    };
});

// ✨ MASSTRANSIT CONFIGURATION - Production-grade consumer
builder.Services.AddMassTransit(x =>
{
    // Register consumers
    x.AddConsumer<ContextUpdatedConsumer>();
    x.AddConsumer<ContextDeletedConsumer>();

    // Configure RabbitMQ transport
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        // Configure retry policy with exponential backoff
        cfg.UseMessageRetry(r =>
        {
            r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
            r.Handle<Exception>(); // Retry on any exception
        });

        // Configure circuit breaker to prevent cascading failures
        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15; // Open circuit after 15 failures
            cb.ActiveThreshold = 10; // Need 10 successes to close
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        // Rate limiting to prevent overwhelming downstream services
        cfg.UseRateLimit(1000, TimeSpan.FromSeconds(1));

        // Configure endpoints
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHostedService<MetricsWebHostService>();

// OpenTelemetry configuration
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SerialMemory.Worker"))
    .WithMetrics(mb =>
    {
        mb.AddMeter(Metrics.MeterName);
        mb.AddPrometheusExporter();
        mb.AddAspNetCoreInstrumentation();
        mb.AddHttpClientInstrumentation();
        mb.AddProcessInstrumentation();
    });

var host = builder.Build();
await host.RunAsync();

// Minimal web host to expose /metrics on port 8081