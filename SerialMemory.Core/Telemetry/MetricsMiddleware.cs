using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Telemetry;

/// <summary>
/// ASP.NET Core middleware for comprehensive HTTP request metrics.
/// Records request count, duration, and status codes with path pattern normalization.
/// </summary>
public sealed class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MetricsMiddleware> _logger;

    public MetricsMiddleware(RequestDelegate next, ILogger<MetricsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip metrics endpoint to avoid recursion
        if (context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Metrics.ActiveHttpConnections.Add(1);
        var success = true;

        try
        {
            await _next(context);
            success = context.Response.StatusCode < 400;
        }
        catch (Exception ex)
        {
            success = false;
            // Record unhandled exception
            Metrics.UnhandledExceptionsTotal.Add(1, new TagList { { "exception_type", ex.GetType().Name } });
            throw;
        }
        finally
        {
            stopwatch.Stop();
            Metrics.ActiveHttpConnections.Add(-1);

            var pathPattern = NormalizePathPattern(context.Request.Path);
            Metrics.RecordHttpRequest(
                context.Request.Method,
                pathPattern,
                context.Response.StatusCode,
                stopwatch.Elapsed.TotalMilliseconds);

            // Record to IPerformanceService if available
            var perfService = context.RequestServices.GetService<IPerformanceService>();
            if (perfService != null)
            {
                var operationName = $"{context.Request.Method}:{pathPattern}";
                perfService.RecordLatency(operationName, stopwatch.Elapsed, success, context.Response.StatusCode);
            }

            // Log slow requests
            if (stopwatch.ElapsedMilliseconds > 1000)
            {
                _logger.LogWarning(
                    "Slow request: {Method} {Path} took {Duration}ms (status: {StatusCode})",
                    context.Request.Method,
                    context.Request.Path,
                    stopwatch.ElapsedMilliseconds,
                    context.Response.StatusCode);
            }
        }
    }

    /// <summary>
    /// Normalizes paths to reduce cardinality (replaces GUIDs and IDs with placeholders)
    /// </summary>
    private static string NormalizePathPattern(PathString path)
    {
        var pathValue = path.Value ?? "/";

        // Replace GUIDs with {id}
        pathValue = System.Text.RegularExpressions.Regex.Replace(
            pathValue,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "{id}");

        // Replace numeric IDs with {id}
        pathValue = System.Text.RegularExpressions.Regex.Replace(
            pathValue,
            @"/\d+(?=/|$)",
            "/{id}");

        return pathValue;
    }
}

/// <summary>
/// Extension methods for registering metrics middleware
/// </summary>
public static class MetricsMiddlewareExtensions
{
    /// <summary>
    /// Adds the metrics middleware to the pipeline.
    /// Should be added early in the pipeline to capture all requests.
    /// </summary>
    public static IApplicationBuilder UseMetricsMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<MetricsMiddleware>();
    }
}
