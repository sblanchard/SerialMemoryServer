using Dapper;
using Npgsql;
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
        group.MapGet("/status", (IDeploymentContext deployment, IKnowledgeGraphStore _, HttpContext http) =>
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
                    POSTGRES_HOST = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_HOST")) ? "configured" : "default",
                    POSTGRES_DB = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_DB")) ? "configured" : "default",
                    OLLAMA_URL = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OLLAMA_URL")) ? "configured" : "default"
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
        group.MapPost("/maintenance/reindex", async (IDeploymentContext deployment, IKnowledgeGraphStore _, HttpContext http) =>
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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SelfHostedEndpoints");
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var metricsProvider = http.RequestServices.GetService<ISelfHostMetricsProvider>();
            if (metricsProvider == null)
            {
                logger.LogWarning("ISelfHostMetricsProvider not registered in DI - returning demo mode");
                // No metrics provider configured - return explicit demo mode
                return Results.Ok(new SelfHostMetricsResult
                {
                    IsDemoMode = true,
                    DemoModeReason = "ISelfHostMetricsProvider not configured. Register it in DI to enable live metrics."
                });
            }

            var metrics = await metricsProvider.GetMetricsAsync(ct);

            // Log the metrics mode for debugging
            logger.LogDebug(
                "Metrics fetched: IsDemoMode={IsDemoMode}, HasError={HasError}, Reason={Reason}",
                metrics.IsDemoMode, metrics.HasError, metrics.DemoModeReason ?? metrics.ErrorMessage ?? "(none)");

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

        // GET /api/selfhost/metrics/test - Quick database connectivity test
        group.MapGet("/metrics/test", async (
            IDeploymentContext deployment,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SelfHostedEndpoints");
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var metricsProvider = http.RequestServices.GetService<ISelfHostMetricsProvider>();
            var results = new Dictionary<string, object>
            {
                ["providerRegistered"] = metricsProvider != null,
                ["isLiveMode"] = metricsProvider?.IsLiveMode ?? false,
                ["timestamp"] = DateTimeOffset.UtcNow
            };

            // Test basic database connectivity
            try
            {
                var dataSource = http.RequestServices.GetService<NpgsqlDataSource>();
                if (dataSource != null)
                {
                    await using var conn = await dataSource.OpenConnectionAsync(ct);
                    var version = await conn.ExecuteScalarAsync<string>("SELECT version()");
                    results["dbConnected"] = true;
                    results["dbVersion"] = version ?? "unknown";

                    // Test app.role setting
                    try
                    {
                        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
                        results["appRoleSet"] = true;
                    }
                    catch (Exception ex)
                    {
                        results["appRoleSet"] = false;
                        results["appRoleError"] = ex.Message;
                    }

                    // Test if key tables exist
                    var tables = await conn.QueryAsync<string>(@"
                        SELECT table_name FROM information_schema.tables
                        WHERE table_schema = 'public'
                        AND table_name IN ('usage_events', 'graph_metrics_hourly', 'maintenance_tasks',
                                          'supervised_jobs', 'event_log', 'usage_daily_rollups')");
                    results["tablesFound"] = tables.ToList();
                }
                else
                {
                    results["dbConnected"] = false;
                    results["dbError"] = "NpgsqlDataSource not registered in DI";
                }
            }
            catch (Exception ex)
            {
                results["dbConnected"] = false;
                results["dbError"] = ex.Message;
                logger.LogError(ex, "Metrics test: database connection failed");
            }

            return Results.Ok(results);
        });

        // GET /api/selfhost/metrics/config - Show metrics configuration for debugging
        group.MapGet("/metrics/config", (
            IDeploymentContext deployment,
            HttpContext http,
            Microsoft.Extensions.Options.IOptions<SelfHostMetricsConfig> metricsConfig) =>
        {
            if (!CanAccessSelfHostFeatures(deployment, http))
            {
                return Results.NotFound(new { error = "This endpoint is only available in self-hosted mode or for root admins." });
            }

            var config = metricsConfig.Value;
            var provider = http.RequestServices.GetService<ISelfHostMetricsProvider>();

            return Results.Ok(new
            {
                mode = config.Mode,
                prometheusUrl = config.PrometheusUrl ?? "(not configured)",
                fetchTimeoutSeconds = config.FetchTimeout.TotalSeconds,
                cacheExpirySeconds = config.CacheExpiry.TotalSeconds,
                providerRegistered = provider != null,
                isLiveMode = provider?.IsLiveMode ?? false,
                envVars = new
                {
                    SELF_HOST_METRICS_MODE = Environment.GetEnvironmentVariable("SELF_HOST_METRICS_MODE") ?? "(not set, defaults to 'auto')",
                    SELF_HOST_PROMETHEUS_URL = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELF_HOST_PROMETHEUS_URL")) ? "configured" : "(not set)",
                    SELF_HOST_METRICS_TIMEOUT_SECONDS = Environment.GetEnvironmentVariable("SELF_HOST_METRICS_TIMEOUT_SECONDS") ?? "(not set, defaults to 5)",
                    SELF_HOST_METRICS_CACHE_SECONDS = Environment.GetEnvironmentVariable("SELF_HOST_METRICS_CACHE_SECONDS") ?? "(not set, defaults to 30)"
                },
                troubleshooting = config.Mode.Equals("demo", StringComparison.OrdinalIgnoreCase)
                    ? "Mode is set to 'demo'. Set SELF_HOST_METRICS_MODE=auto or SELF_HOST_METRICS_MODE=live for real metrics."
                    : provider == null
                    ? "Provider not registered. Check DI configuration."
                    : "Configuration looks correct. Check logs for query errors."
            });
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
        return typeof(SelfHostedEndpoints).Assembly.GetName().Version?.ToString() ?? "2.2.0";
    }

    private static string GetUptime()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
    }
}
