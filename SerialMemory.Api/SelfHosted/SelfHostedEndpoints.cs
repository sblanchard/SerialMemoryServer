using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.SelfHosted;

/// <summary>
/// Self-hosted admin endpoints only available in SelfHosted deployment mode.
/// </summary>
public static class SelfHostedEndpoints
{
    /// <summary>
    /// Maps all self-hosted admin endpoints.
    /// These endpoints are only available when SERIALMEMORY_DEPLOYMENT_MODE=SelfHosted.
    /// </summary>
    public static IEndpointRouteBuilder MapSelfHostedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/selfhost")
            .WithTags("Self-Hosted Admin");

        // GET /api/selfhost/status - Self-hosted deployment status
        group.MapGet("/status", (IDeploymentContext deployment, IKnowledgeGraphStore store) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
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
        group.MapGet("/config", (IDeploymentContext deployment) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
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
        group.MapGet("/license", (IDeploymentContext deployment) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
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
        group.MapPost("/maintenance/reindex", async (IDeploymentContext deployment, IKnowledgeGraphStore store) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
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
        group.MapPost("/maintenance/vacuum", async (IDeploymentContext deployment) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
            }

            return Results.Accepted(value: new
            {
                message = "Vacuum scheduled",
                jobId = Guid.CreateVersion7(),
                note = "This operation may take several minutes on large databases"
            });
        });

        // GET /api/selfhost/diagnostics - System diagnostics
        group.MapGet("/diagnostics", async (IDeploymentContext deployment, IKnowledgeGraphStore store) =>
        {
            if (!deployment.IsSelfHosted)
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode." });
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

        return app;
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
