using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SerialMemory.Core.Telemetry;

/// <summary>
/// Extension methods for configuring production-grade telemetry in SerialMemory services.
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>
    /// Adds comprehensive OpenTelemetry metrics and tracing to the service.
    /// Includes Prometheus exporter, runtime metrics, and custom SerialMemory metrics.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="serviceName">The service name for resource identification (e.g., "SerialMemory.Api")</param>
    /// <param name="serviceVersion">Optional service version</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSerialMemoryTelemetry(
        this IServiceCollection services,
        string serviceName,
        string? serviceVersion = null)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(
                    serviceName: serviceName,
                    serviceVersion: serviceVersion ?? "1.0.0",
                    serviceInstanceId: Environment.MachineName);

                resource.AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production",
                    ["host.name"] = Environment.MachineName
                });
            })
            .WithMetrics(metrics =>
            {
                // SerialMemory custom metrics
                metrics.AddMeter(Metrics.MeterName);

                // Prometheus exporter (exposes /metrics endpoint)
                metrics.AddPrometheusExporter();

                // .NET Runtime metrics (GC, threadpool, etc.)
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();

                // Process metrics (CPU, memory, handles)
                metrics.AddProcessInstrumentation();

                // HTTP client instrumentation
                metrics.AddHttpClientInstrumentation();

                // ASP.NET Core instrumentation
                metrics.AddAspNetCoreInstrumentation();

                // Configure histogram boundaries for better percentile accuracy
                metrics.AddView(
                    "http_request_duration_ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 10000]
                    });

                metrics.AddView(
                    "db_query_duration_ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000]
                    });

                metrics.AddView(
                    "search_duration_ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [10, 25, 50, 100, 250, 500, 1000, 2500, 5000]
                    });

                metrics.AddView(
                    "embedding_duration_ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [10, 25, 50, 100, 250, 500, 1000, 2500]
                    });
            })
            .WithTracing(tracing =>
            {
                // ASP.NET Core request tracing
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.Filter = httpContext =>
                    {
                        // Don't trace health checks and metrics endpoints
                        var path = httpContext.Request.Path.Value ?? "";
                        return !path.StartsWith("/health") && !path.StartsWith("/metrics");
                    };
                });

                // HTTP client tracing
                tracing.AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = true;
                });
            });

        return services;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint at /metrics.
    /// Call after UseRouting() and before UseEndpoints().
    /// </summary>
    public static IApplicationBuilder UseSerialMemoryMetrics(this IApplicationBuilder app)
    {
        // Add custom metrics middleware for detailed HTTP tracking
        app.UseMetricsMiddleware();

        return app;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint for WebApplication.
    /// </summary>
    public static WebApplication MapSerialMemoryMetrics(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();
        return app;
    }
}
