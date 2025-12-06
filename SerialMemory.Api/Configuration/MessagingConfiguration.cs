using MassTransit;
using SerialMemory.Api.Realtime;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for messaging service configuration (MassTransit, SignalR).
/// </summary>
public static class MessagingConfiguration
{
    /// <summary>
    /// Adds MassTransit with RabbitMQ configuration.
    /// </summary>
    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        try
        {
            var rabbitHost = configuration["RabbitMq:Host"] ?? "localhost";
            var rabbitUser = configuration["RabbitMq:User"] ?? "guest";
            var rabbitPass = configuration["RabbitMq:Password"] ?? "guest";
            var rabbitVHost = configuration["RabbitMq:VHost"] ?? "/";

            services.AddMassTransit(x =>
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(rabbitHost, rabbitVHost, h =>
                    {
                        h.Username(rabbitUser);
                        h.Password(rabbitPass);
                    });
                    cfg.ConfigureEndpoints(context);
                    cfg.UseMessageRetry(r => r.Exponential(5,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(5)));
                });
            });
            services.AddScoped<MassTransitEventPublisher>();
            Console.WriteLine("[INFO] MassTransit configured with RabbitMQ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] RabbitMQ not available - skipping MassTransit: {ex.Message}");
        }

        return services;
    }

    /// <summary>
    /// Adds SignalR for real-time updates.
    /// </summary>
    public static IServiceCollection AddSignalRMessaging(this IServiceCollection services)
    {
        services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = true;
            o.MaximumReceiveMessageSize = 64 * 1024;
        });

        return services;
    }

    /// <summary>
    /// Adds live event emitter for real-time streaming.
    /// </summary>
    public static IServiceCollection AddLiveEventEmitter(this IServiceCollection services)
    {
        services.AddSingleton<ILiveEventEmitter, LiveEventEmitter>();
        return services;
    }
}
