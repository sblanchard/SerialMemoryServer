using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;
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

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

builder.Services.AddSingleton<IContextStore, RedisContextStore>();

// ✨ MASSTRANSIT CONFIGURATION - Production-grade messaging
builder.Services.AddMassTransit(x =>
{
    // Configure RabbitMQ transport
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        // Automatically configure all endpoints
        cfg.ConfigureEndpoints(context);

        // Enable message retry with exponential backoff
        cfg.UseMessageRetry(r => r.Exponential(5,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5)));

        // NOTE: Message scheduling would require additional package: MassTransit.Quartz or MassTransit.Hangfire
        //cfg.UseMessageScheduling();

        // NOTE: Outbox pattern for transactional messaging (optional for this demo)
        // cfg.UseInMemoryOutbox(context);
    });
});

// Register our event publisher
builder.Services.AddScoped<MassTransitEventPublisher>();

builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
        o.MaximumReceiveMessageSize = 64 * 1024;
    })
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis")!, opts =>
    {
        // Optional: custom channel name for scale-out
        opts.Configuration.ChannelPrefix = "serialmemory:signalr";
    });

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
app.UseSwagger();
app.UseSwaggerUI();

app.MapPrometheusScrapingEndpoint();
app.MapHub<ContextHub>("/hub/context");

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

app.MapPost("/context/{key}", async (string key, HttpRequest req, IContextStore store,
    MassTransitEventPublisher eventPublisher, IHubContext<ContextHub> hub) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    await store.SetAsync(key, body);

    // ✨ Publish strongly-typed event with MassTransit
    await eventPublisher.PublishContextUpdatedAsync(key, body, "api");

    await hub.Clients.Group(key).SendAsync("ContextUpdated", new { key, value = body, tsUtc = DateTime.UtcNow });
    return Results.Ok();
});

app.MapDelete("/context/{key}", async (string key, IContextStore store,
    MassTransitEventPublisher eventPublisher, IHubContext<ContextHub> hub) =>
{
    await store.DeleteAsync(key);

    // ✨ Publish strongly-typed event with MassTransit
    await eventPublisher.PublishContextDeletedAsync(key, "api");

    await hub.Clients.Group(key).SendAsync("ContextDeleted", new { key, tsUtc = DateTime.UtcNow });
    return Results.Ok();
});

app.Run();