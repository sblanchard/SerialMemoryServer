using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// SignalR hub for real-time reasoning execution events.
/// Streams DAG trace updates and execution completion notifications.
/// </summary>
public sealed class ReasoningHub : Hub
{
    private readonly ILogger<ReasoningHub> _logger;
    private readonly IDeploymentContext _deploymentContext;
    private readonly ITenantContext _tenantContext;
    private readonly IMutableTenantContext? _mutableTenantContext;
    private readonly byte[]? _internalTokenKey;
    private readonly string _internalTokenIssuer;
    private readonly string _internalTokenAudience;

    public ReasoningHub(
        ILogger<ReasoningHub> logger,
        IDeploymentContext deploymentContext,
        ITenantContext tenantContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _deploymentContext = deploymentContext;
        _tenantContext = tenantContext;
        _mutableTenantContext = tenantContext as IMutableTenantContext;

        _internalTokenIssuer = configuration["JWT_ISSUER"] ?? "serialmemory";
        _internalTokenAudience = configuration["JWT_AUDIENCE"] ?? "serialmemory-api";

        var jwtSecret = configuration["JWT_SECRET"];
        if (!string.IsNullOrEmpty(jwtSecret))
        {
            _internalTokenKey = System.Text.Encoding.UTF8.GetBytes(jwtSecret);
        }
        else
        {
            var keyBase64 = configuration["INTERNAL_TOKEN_KEY"];
            if (!string.IsNullOrEmpty(keyBase64))
            {
                _internalTokenKey = Convert.FromBase64String(keyBase64);
            }
            else
            {
                _internalTokenKey = System.Text.Encoding.UTF8.GetBytes("default-development-secret-32chars!!");
                _logger.LogWarning("JWT_SECRET not configured for ReasoningHub. Using default development key.");
            }
        }
    }

    public override async Task OnConnectedAsync()
    {
        var accessToken = Context.GetHttpContext()?.Request.Query["access_token"].FirstOrDefault();
        var claims = ValidateInternalToken(accessToken);

        if (claims == null && _deploymentContext.IsSaaS)
        {
            _logger.LogWarning("ReasoningHub connection rejected: Invalid or missing internal token");
            Context.Abort();
            return;
        }

        if (claims != null && _mutableTenantContext != null)
        {
            _mutableTenantContext.SetContext(
                tenantId: claims.TenantId,
                workspaceId: claims.WorkspaceId,
                userId: claims.UserId,
                sessionId: null,
                isLabMode: false,
                scopes: new[] { "dashboard", "reasoning" });

            Context.Items["TenantId"] = claims.TenantId;
            Context.Items["UserId"] = claims.UserId;
            Context.Items["WorkspaceId"] = claims.WorkspaceId;
        }

        _logger.LogInformation(
            "ReasoningHub client connected: {ConnectionId} (Tenant: {TenantId})",
            Context.ConnectionId,
            claims?.TenantId ?? "anonymous");

        // Auto-subscribe to tenant reasoning events
        if (claims != null)
        {
            var tenantGroup = GetTenantGroup(claims.TenantId);
            await Groups.AddToGroupAsync(Context.ConnectionId, tenantGroup);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("ReasoningHub client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to updates for a specific reasoning execution.
    /// Receives NewStepAdded and ExecutionCompleted events.
    /// </summary>
    public async Task SubscribeToExecution(string executionId)
    {
        if (!Guid.TryParse(executionId, out var execGuid))
        {
            await Clients.Caller.SendAsync("Error", new { code = "InvalidExecutionId", message = "Invalid execution ID format" });
            return;
        }

        var group = GetExecutionGroup(execGuid);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        _logger.LogDebug("Client {ConnectionId} subscribed to execution {ExecutionId}",
            Context.ConnectionId, executionId);

        await Clients.Caller.SendAsync("SubscribedToExecution", executionId);
    }

    /// <summary>
    /// Unsubscribe from a specific reasoning execution.
    /// </summary>
    public async Task UnsubscribeFromExecution(string executionId)
    {
        if (!Guid.TryParse(executionId, out var execGuid))
        {
            return;
        }

        var group = GetExecutionGroup(execGuid);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

        _logger.LogDebug("Client {ConnectionId} unsubscribed from execution {ExecutionId}",
            Context.ConnectionId, executionId);

        await Clients.Caller.SendAsync("UnsubscribedFromExecution", executionId);
    }

    /// <summary>
    /// Subscribe to all reasoning events for the current tenant.
    /// </summary>
    public async Task SubscribeToAllReasoningEvents()
    {
        var tenantId = Context.Items["TenantId"] as string ?? _tenantContext.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            await Clients.Caller.SendAsync("Error", new { code = "NoTenant", message = "No tenant context available" });
            return;
        }

        var group = GetTenantGroup(tenantId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        _logger.LogDebug("Client {ConnectionId} subscribed to all reasoning events for tenant {TenantId}",
            Context.ConnectionId, tenantId);

        await Clients.Caller.SendAsync("SubscribedToAllReasoningEvents", tenantId);
    }

    /// <summary>
    /// Ping for connection health check.
    /// </summary>
    public async Task Ping()
    {
        await Clients.Caller.SendAsync("Pong", DateTimeOffset.UtcNow);
    }

    #region Group Naming

    private static string GetExecutionGroup(Guid executionId) => $"reasoning:execution:{executionId}";
    private static string GetTenantGroup(string tenantId) => $"reasoning:tenant:{tenantId}";

    #endregion

    #region Token Validation

    private ReasoningTokenClaims? ValidateInternalToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || _internalTokenKey == null)
            return null;

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var securityKey = new SymmetricSecurityKey(_internalTokenKey);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = true,
                ValidIssuer = _internalTokenIssuer,
                ValidateAudience = true,
                ValidAudience = _internalTokenAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

            var tokenTypeClaim = principal.FindFirst("token_type")?.Value;
            if (tokenTypeClaim != "internal")
            {
                _logger.LogWarning("ReasoningHub token validation failed: Invalid token_type claim");
                return null;
            }

            return new ReasoningTokenClaims
            {
                UserId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? "",
                TenantId = principal.FindFirst("tenant_id")?.Value ?? "",
                WorkspaceId = principal.FindFirst("workspace_id")?.Value ?? "default",
                Role = principal.FindFirst("role")?.Value ?? ""
            };
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "ReasoningHub internal token validation failed");
            return null;
        }
    }

    private sealed class ReasoningTokenClaims
    {
        public required string TenantId { get; init; }
        public required string UserId { get; init; }
        public string WorkspaceId { get; init; } = "default";
        public required string Role { get; init; }
    }

    #endregion
}

/// <summary>
/// Service for broadcasting reasoning events to connected SignalR clients.
/// Injected as singleton and used by ReasoningRunService.
/// </summary>
public sealed class ReasoningHubBroadcaster
{
    private readonly IHubContext<ReasoningHub> _hubContext;
    private readonly ILogger<ReasoningHubBroadcaster> _logger;

    public ReasoningHubBroadcaster(
        IHubContext<ReasoningHub> hubContext,
        ILogger<ReasoningHubBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Broadcast when a new reasoning execution is started.
    /// </summary>
    public async Task BroadcastExecutionStartedAsync(Guid executionId, Guid tenantId, ReasoningExecution execution)
    {
        var tenantGroup = $"reasoning:tenant:{tenantId}";
        var executionGroup = $"reasoning:execution:{executionId}";

        var payload = new
        {
            type = "ExecutionStarted",
            executionId = executionId.ToString(),
            tenantId = tenantId.ToString(),
            inputType = execution.InputType.ToString(),
            inputReference = execution.InputReference,
            startedAt = execution.StartedAt
        };

        await Task.WhenAll(
            _hubContext.Clients.Group(tenantGroup).SendAsync("ExecutionStarted", payload),
            _hubContext.Clients.Group(executionGroup).SendAsync("ExecutionStarted", payload));

        _logger.LogDebug("Broadcast ExecutionStarted for {ExecutionId}", executionId);
    }

    /// <summary>
    /// Broadcast when a new trace step is added to an execution.
    /// This is the DAG node being added.
    /// </summary>
    public async Task BroadcastNewStepAddedAsync(Guid executionId, Guid tenantId, ReasoningTrace trace)
    {
        var tenantGroup = $"reasoning:tenant:{tenantId}";
        var executionGroup = $"reasoning:execution:{executionId}";

        var payload = new
        {
            type = "NewStepAdded",
            executionId = executionId.ToString(),
            trace = new
            {
                id = trace.Id.ToString(),
                stepId = trace.StepId,
                parentStepIds = trace.ParentStepIds,
                stepOrder = trace.StepOrder,
                role = trace.Role.ToString(),
                modelName = trace.ModelName,
                modelVersion = trace.ModelVersion,
                durationMs = trace.DurationMs,
                success = trace.Success,
                errorMessage = trace.ErrorMessage,
                startedAt = trace.StartedAt,
                completedAt = trace.CompletedAt
            }
        };

        await Task.WhenAll(
            _hubContext.Clients.Group(tenantGroup).SendAsync("NewStepAdded", payload),
            _hubContext.Clients.Group(executionGroup).SendAsync("NewStepAdded", payload));

        _logger.LogDebug("Broadcast NewStepAdded for {ExecutionId}, step {StepId}", executionId, trace.StepId);
    }

    /// <summary>
    /// Broadcast when an execution is completed (success or failure).
    /// </summary>
    public async Task BroadcastExecutionCompletedAsync(Guid executionId, Guid tenantId, ReasoningExecution execution)
    {
        var tenantGroup = $"reasoning:tenant:{tenantId}";
        var executionGroup = $"reasoning:execution:{executionId}";

        var payload = new
        {
            type = "ExecutionCompleted",
            executionId = executionId.ToString(),
            tenantId = tenantId.ToString(),
            status = execution.Status.ToString(),
            modelsUsed = execution.ModelsUsed,
            successfulModels = execution.SuccessfulModels,
            totalDurationMs = execution.TotalDurationMs,
            overallConfidence = execution.OverallConfidence,
            insightCount = execution.InsightCount,
            disagreementCount = execution.DisagreementCount,
            errorMessage = execution.ErrorMessage,
            startedAt = execution.StartedAt,
            completedAt = execution.CompletedAt,
            // Include traces summary for DAG rendering
            traces = execution.Traces.Select(t => new
            {
                id = t.Id.ToString(),
                stepId = t.StepId,
                parentStepIds = t.ParentStepIds,
                stepOrder = t.StepOrder,
                role = t.Role.ToString(),
                modelName = t.ModelName,
                durationMs = t.DurationMs,
                success = t.Success
            }).ToList()
        };

        await Task.WhenAll(
            _hubContext.Clients.Group(tenantGroup).SendAsync("ExecutionCompleted", payload),
            _hubContext.Clients.Group(executionGroup).SendAsync("ExecutionCompleted", payload));

        _logger.LogDebug("Broadcast ExecutionCompleted for {ExecutionId}, status {Status}", executionId, execution.Status);
    }

    /// <summary>
    /// Broadcast progress update during long-running reasoning.
    /// </summary>
    public async Task BroadcastProgressAsync(Guid executionId, Guid tenantId, int completedSteps, int totalSteps, string currentStep)
    {
        var tenantGroup = $"reasoning:tenant:{tenantId}";
        var executionGroup = $"reasoning:execution:{executionId}";

        var payload = new
        {
            type = "Progress",
            executionId = executionId.ToString(),
            completedSteps,
            totalSteps,
            currentStep,
            percentComplete = totalSteps > 0 ? (completedSteps * 100 / totalSteps) : 0
        };

        await Task.WhenAll(
            _hubContext.Clients.Group(tenantGroup).SendAsync("Progress", payload),
            _hubContext.Clients.Group(executionGroup).SendAsync("Progress", payload));
    }
}
