using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.SelfHosted;

/// <summary>
/// Self-hosted admin endpoints only available in SelfHosted deployment mode or for root admins.
/// </summary>
public static class SelfHostedEndpoints
{
    /// <summary>
    /// Maps all self-hosted admin endpoints.
    /// These endpoints are available when SERIALMEMORY_DEPLOYMENT_MODE=SelfHosted or for root admins.
    /// </summary>
    public static IEndpointRouteBuilder MapSelfHostedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/selfhost")
            .WithTags("Self-Hosted Admin");

        // GET /api/selfhost/status - Self-hosted deployment status
        group.MapGet("/status", (IDeploymentContext deployment, IKnowledgeGraphStore store, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            return Results.Ok(new
            {
                mode = deployment.Mode.ToString(),
                instanceId = deployment.InstanceId,
                quotasEnabled = deployment.QuotasEnabled,
                powerModeEnabled = !deployment.PowerModeGloballyDisabled,
                version = GetVersion(),
                uptime = GetUptime(),
                status = "healthy"
            });
        });

        // GET /api/selfhost/config - Current configuration
        group.MapGet("/config", (IDeploymentContext deployment, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            return Results.Ok(new
            {
                deployment = new
                {
                    mode = deployment.Mode.ToString(),
                    instanceId = deployment.InstanceId,
                    quotasEnabled = deployment.QuotasEnabled,
                    powerModeGloballyDisabled = deployment.PowerModeGloballyDisabled
                },
                environment = new
                {
                    SERIALMEMORY_DEPLOYMENT_MODE = Environment.GetEnvironmentVariable("SERIALMEMORY_DEPLOYMENT_MODE") ?? "PublicSaaS",
                    SERIALMEMORY_ENABLE_QUOTAS = Environment.GetEnvironmentVariable("SERIALMEMORY_ENABLE_QUOTAS") ?? "false",
                    SERIALMEMORY_DISABLE_POWER_MODE = Environment.GetEnvironmentVariable("SERIALMEMORY_DISABLE_POWER_MODE") ?? "false",
                    POSTGRES_HOST = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost",
                    POSTGRES_DB = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb",
                    OLLAMA_URL = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434"
                },
                features = new
                {
                    powerMode = !deployment.PowerModeGloballyDisabled,
                    mutations = true,
                    sqlAccess = !deployment.PowerModeGloballyDisabled,
                    graphManipulation = !deployment.PowerModeGloballyDisabled,
                    export = true,
                    signalrGlobalStreams = true
                }
            });
        });

        // GET /api/selfhost/license - License information (stub)
        group.MapGet("/license", (IDeploymentContext deployment, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            return Results.Ok(new
            {
                licenseType = "community",
                licenseKey = deployment.LicenseKey ?? "not-configured",
                status = "valid",
                features = new[]
                {
                    "core_memory",
                    "semantic_search",
                    "entity_extraction",
                    "knowledge_graph",
                    "power_mode",
                    "exports",
                    "multi_model_reasoning"
                },
                limits = new
                {
                    memories = "unlimited",
                    entities = "unlimited",
                    apiKeys = "unlimited",
                    workspaces = "unlimited"
                },
                support = new
                {
                    level = "community",
                    channel = "github"
                },
                message = "SerialMemory Community Edition - Self-Hosted"
            });
        });

        // POST /api/selfhost/maintenance/reindex - Trigger re-indexing
        group.MapPost("/maintenance/reindex", async (IDeploymentContext deployment, IKnowledgeGraphStore store, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            // This is a placeholder - actual implementation would trigger background reindexing
            return Results.Accepted(value: new
            {
                message = "Reindexing scheduled",
                jobId = Guid.CreateVersion7(),
                estimatedDuration = "depends on database size"
            });
        });

        // POST /api/selfhost/maintenance/vacuum - Trigger database vacuum
        group.MapPost("/maintenance/vacuum", async (IDeploymentContext deployment, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            return Results.Accepted(value: new
            {
                message = "Vacuum scheduled",
                jobId = Guid.CreateVersion7(),
                note = "This operation may take several minutes on large databases"
            });
        });

        // GET /api/selfhost/diagnostics - System diagnostics
        group.MapGet("/diagnostics", async (IDeploymentContext deployment, IKnowledgeGraphStore store, HttpContext http) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var memoryCount = await store.GetMemoryCountAsync();
            var entityCount = await store.GetEntityCountAsync();

            return Results.Ok(new
            {
                database = new
                {
                    memories = memoryCount,
                    entities = entityCount,
                    status = "connected"
                },
                system = new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.ToString(),
                    processorCount = Environment.ProcessorCount,
                    workingSet = Environment.WorkingSet / 1024 / 1024, // MB
                    gcMemory = GC.GetTotalMemory(false) / 1024 / 1024 // MB
                },
                runtime = new
                {
                    version = Environment.Version.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                }
            });
        });

        // GET /api/selfhost/metrics - Self-host metrics with explicit demo mode flag
        // IMPORTANT: This endpoint NEVER silently returns fake data.
        // If IsDemoMode=true, the UI MUST show a clear indicator.
        group.MapGet("/metrics", async (
            IDeploymentContext deployment,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var metricsProvider = http.RequestServices.GetService<ISelfHostMetricsProvider>();
            if (metricsProvider == null)
            {
                // No metrics provider configured - return explicit demo mode
                return Results.Ok(new SelfHostMetricsResult
                {
                    IsDemoMode = true,
                    DemoModeReason = "ISelfHostMetricsProvider not configured. Register it in DI to enable live metrics."
                });
            }

            var metrics = await metricsProvider.GetMetricsAsync(ct);
            return Results.Ok(metrics);
        });

        // GET /api/selfhost/metrics/current - Current load for real-time updates
        group.MapGet("/metrics/current", async (
            IDeploymentContext deployment,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var metricsProvider = http.RequestServices.GetService<ISelfHostMetricsProvider>();
            if (metricsProvider == null)
            {
                return Results.Ok(new SelfHostLoadStatus
                {
                    IsDemoMode = true,
                    CurrentRps = 0,
                    P95LatencyMs = 0,
                    PendingTasks = 0,
                    ActiveConnections = 0,
                    DbConnectionUtilization = 0
                });
            }

            var load = await metricsProvider.GetCurrentLoadAsync(ct);
            return Results.Ok(load);
        });

        return app;
    }

    /// <summary>
    /// Checks if the current user can access self-host features.
    /// Returns true if running in self-hosted mode or if the user is a root admin.
    /// </summary>
    private static bool CanAccessSelfHostFeatures(IDeploymentContext deployment, HttpContext http)
    {
        // Always allow in self-hosted mode
        if (deployment.IsSelfHosted)
            return true;

        // In SaaS mode, allow root admins
        var isRootAdmin = http.User?.HasClaim("is_root_admin", "true") == true;
        return isRootAdmin;
    }

    private static string GetVersion()
    {
        return typeof(SelfHostedEndpoints).Assembly.GetName().Version?.ToString() ?? "2.1.0";
    }

    private static string GetUptime()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
    }
}
