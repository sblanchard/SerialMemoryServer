using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SerialMemory.Core.Telemetry;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for observability configuration (OpenTelemetry, metrics, tracing).
/// </summary>
public static class ObservabilityConfiguration
{
    /// <summary>
    /// Adds OpenTelemetry metrics and tracing with Prometheus exporter.
    /// </summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        string serviceName = "SerialMemory.Api")
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(mb =>
            {
                mb.AddMeter(Metrics.MeterName);
                mb.AddPrometheusExporter();
                mb.AddProcessInstrumentation();
                mb.AddHttpClientInstrumentation();
                mb.AddAspNetCoreInstrumentation();
            })
            .WithTracing(tb =>
            {
                tb.AddAspNetCoreInstrumentation();
                tb.AddHttpClientInstrumentation();
            });

        Console.WriteLine($"[INFO] OpenTelemetry configured for {serviceName}");

        return services;
    }

    /// <summary>
    /// Maps the Prometheus metrics endpoint.
    /// </summary>
    public static WebApplication MapMetricsEndpoint(this WebApplication app)
    {
        app.UseOpenTelemetryPrometheusScrapingEndpoint();
        return app;
    }
}
