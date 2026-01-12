using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.KillSwitch;

/// <summary>
/// Middleware that enforces kill switch states.
/// MUST run BEFORE quota enforcement.
/// </summary>
public sealed class KillSwitchMiddleware(RequestDelegate next, ILogger<KillSwitchMiddleware> logger)
{
    // Ingest endpoints that are blocked by TenantNoIngest mode
    private static readonly HashSet<string> IngestEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/memory/ingest",
        "/api/memories",           // POST only
        "/api/memory/batch",
        "/api/memory/import",
        "/api/memories/batch",
        "/api/memories/import",
        "/api/graph/ingest"
    };

    // Methods considered "write" operations
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST",
        "PUT",
        "PATCH",
        "DELETE"
    };

    public async Task InvokeAsync(HttpContext context, IKillSwitchService killSwitchService)
    {
        // Skip health check endpoints
        if (IsHealthCheckEndpoint(context.Request.Path))
        {
            await next(context);
            return;
        }

        // Get tenant context if available
        var tenantContext = context.RequestServices.GetService(typeof(ITenantContext)) as ITenantContext;
        var tenantId = tenantContext?.TenantId;

        // Get API key ID from context if available (set by auth middleware)
        var apiKeyId = context.Items.TryGetValue("ApiKeyId", out var keyId) ? keyId?.ToString() : null;

        // Get kill switch state
        var state = await killSwitchService.GetStateAsync(tenantId, apiKeyId, context.RequestAborted);

        // Check for blocking conditions
        if (state.GlobalOff)
        {
            logger.LogWarning("Request blocked by global kill switch: {Path}", context.Request.Path);
            await WriteErrorResponseAsync(context, 503, "GLOBAL_SERVICE_DISABLED", state.GetEffectiveReason(), state.ExpiresAt);
            return;
        }

        if (state.TenantOff)
        {
            logger.LogWarning("Request blocked for tenant {TenantId}: tenant disabled", tenantId);
            await WriteErrorResponseAsync(context, 503, "TENANT_DISABLED", state.GetEffectiveReason(), state.ExpiresAt);
            return;
        }

        if (state.ApiKeyDisabled)
        {
            logger.LogWarning("Request blocked: API key disabled");
            await WriteErrorResponseAsync(context, 503, "API_KEY_DISABLED", state.GetEffectiveReason(), null);
            return;
        }

        // Check for read-only mode
        if (state.IsReadOnly && IsWriteOperation(context))
        {
            var errorCode = state.GlobalReadOnly ? "GLOBAL_READ_ONLY" : "TENANT_READ_ONLY";
            logger.LogWarning("Write operation blocked by {Mode} mode: {Path}",
                state.GlobalReadOnly ? "global read-only" : "tenant read-only",
                context.Request.Path);
            await WriteErrorResponseAsync(context, 503, errorCode, state.GetEffectiveReason(), state.ExpiresAt);
            return;
        }

        // Check for no-ingest mode
        if (state.TenantNoIngest && IsIngestOperation(context))
        {
            logger.LogWarning("Ingest operation blocked for tenant {TenantId}: {Path}", tenantId, context.Request.Path);
            await WriteErrorResponseAsync(context, 503, "INGEST_DISABLED", state.GetEffectiveReason(), state.ExpiresAt);
            return;
        }

        // Request is allowed, continue pipeline
        await next(context);
    }

    private static bool IsHealthCheckEndpoint(PathString path)
    {
        return path.StartsWithSegments("/health") ||
               path.StartsWithSegments("/healthz") ||
               path.StartsWithSegments("/ready") ||
               path.StartsWithSegments("/live");
    }

    private static bool IsWriteOperation(HttpContext context)
    {
        return WriteMethods.Contains(context.Request.Method);
    }

    private static bool IsIngestOperation(HttpContext context)
    {
        if (!WriteMethods.Contains(context.Request.Method))
            return false;

        var path = context.Request.Path.Value ?? "";

        // Check explicit ingest endpoints
        foreach (var endpoint in IngestEndpoints)
        {
            if (path.StartsWith(endpoint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // POST to /api/memories is ingest
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            (path.Equals("/api/memories", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/memories/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // MCP memory_ingest tool
        return path.Contains("memory_ingest", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteErrorResponseAsync(
        HttpContext context,
        int statusCode,
        string errorCode,
        string reason,
        DateTimeOffset? resumesAt)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = new KillSwitchErrorResponse
        {
            Error = "SERVICE_DISABLED",
            Code = errorCode,
            Reason = reason,
            ResumesAt = resumesAt?.ToString("O")
        };

        // Add Retry-After header if we know when service resumes
        if (resumesAt.HasValue)
        {
            var retryAfterSeconds = Math.Max(1, (int)(resumesAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        }

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private sealed class KillSwitchErrorResponse
    {
        public string Error { get; set; } = "";
        public string Code { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? ResumesAt { get; set; }
    }
}

/// <summary>
/// Extension methods for registering kill switch middleware.
/// </summary>
public static class KillSwitchMiddlewareExtensions
{
    /// <summary>
    /// Adds kill switch middleware to the pipeline.
    /// MUST be registered BEFORE quota enforcement middleware.
    /// </summary>
    public static IApplicationBuilder UseKillSwitch(this IApplicationBuilder app)
    {
        return app.UseMiddleware<KillSwitchMiddleware>();
    }
}
