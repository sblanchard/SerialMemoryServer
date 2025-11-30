using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// Real-time SignalR hub for live streaming of reasoning, security, and graph events.
/// Deployment-aware: In SaaS mode, streams are tenant-scoped. In SelfHosted mode, global streams are available.
/// Requires internal JWT token for authentication (passed via query string: ?access_token=xxx).
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly ILogger<LiveHub> _logger;
    private readonly IDeploymentContext _deploymentContext;
    private readonly ITenantContext _tenantContext;
    private readonly IMutableTenantContext? _mutableTenantContext;
    private readonly byte[]? _internalTokenKey;

    private const string InternalTokenIssuer = "serialmemory-web";
    private const string InternalTokenAudience = "serialmemory-internal";

    public LiveHub(
        ILogger<LiveHub> logger,
        IDeploymentContext deploymentContext,
        ITenantContext tenantContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _deploymentContext = deploymentContext;
        _tenantContext = tenantContext;
        _mutableTenantContext = tenantContext as IMutableTenantContext;

        // Load internal token signing key
        var keyBase64 = configuration["INTERNAL_TOKEN_KEY"];
        if (!string.IsNullOrEmpty(keyBase64))
        {
            _internalTokenKey = Convert.FromBase64String(keyBase64);
        }
    }

    public override async Task OnConnectedAsync()
    {
        // Get access token from query string (standard SignalR pattern)
        var accessToken = Context.GetHttpContext()?.Request.Query["access_token"].FirstOrDefault();

        // Validate internal token
        var claims = ValidateInternalToken(accessToken);
        if (claims == null && _deploymentContext.IsSaaS)
        {
            // SaaS mode requires authentication
            _logger.LogWarning("SignalR connection rejected: Invalid or missing internal token");
            Context.Abort();
            return;
        }

        // Set tenant context from validated token
        if (claims != null && _mutableTenantContext != null)
        {
            _mutableTenantContext.SetContext(
                tenantId: claims.TenantId,
                workspaceId: claims.WorkspaceId,
                userId: claims.UserId,
                sessionId: null,
                isLabMode: false,
                scopes: new[] { "dashboard" });

            // Store claims in connection items for later use
            Context.Items["TenantId"] = claims.TenantId;
            Context.Items["UserId"] = claims.UserId;
            Context.Items["WorkspaceId"] = claims.WorkspaceId;
        }

        _logger.LogInformation(
            "Client connected: {ConnectionId} (Mode: {Mode}, Tenant: {TenantId})",
            Context.ConnectionId,
            _deploymentContext.Mode,
            claims?.TenantId ?? "anonymous");

        // In SaaS mode, auto-subscribe to tenant-specific group
        if (_deploymentContext.IsSaaS && claims != null)
        {
            var tenantGroup = $"tenant:{claims.TenantId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, tenantGroup);
            _logger.LogDebug("Auto-subscribed {ConnectionId} to tenant group {TenantGroup}",
                Context.ConnectionId, tenantGroup);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to a specific stream.
    /// In SaaS mode: streams are automatically tenant-scoped.
    /// In SelfHosted mode: global streams are available.
    /// </summary>
    public async Task Subscribe(string stream)
    {
        var effectiveStream = GetEffectiveStreamName(stream);
        await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
        _logger.LogDebug("Client {ConnectionId} subscribed to {Stream} (effective: {EffectiveStream})",
            Context.ConnectionId, stream, effectiveStream);
        await Clients.Caller.SendAsync("Subscribed", stream);
    }

    /// <summary>
    /// Unsubscribe from a stream
    /// </summary>
    public async Task Unsubscribe(string stream)
    {
        var effectiveStream = GetEffectiveStreamName(stream);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, effectiveStream);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from {Stream}", Context.ConnectionId, stream);
        await Clients.Caller.SendAsync("Unsubscribed", stream);
    }

    /// <summary>
    /// Subscribe to all streams at once
    /// </summary>
    public async Task SubscribeAll()
    {
        var streams = new[]
        {
            "reasoning.progress", "reasoning.results", "security.anomalies", "graph.changes", "jobs.progress",
            "introspection.full", "introspection.jobs", "introspection.traces", "introspection.security", "introspection.mutations"
        };
        foreach (var stream in streams)
        {
            var effectiveStream = GetEffectiveStreamName(stream);
            await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
        }
        _logger.LogDebug("Client {ConnectionId} subscribed to all streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedAll", streams);
    }

    /// <summary>
    /// Subscribe to introspection streams only
    /// </summary>
    public async Task SubscribeIntrospection()
    {
        var streams = new[]
        {
            "introspection.full", "introspection.jobs", "introspection.traces",
            "introspection.security", "introspection.mutations"
        };
        foreach (var stream in streams)
        {
            var effectiveStream = GetEffectiveStreamName(stream);
            await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
        }
        _logger.LogDebug("Client {ConnectionId} subscribed to introspection streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedIntrospection", streams);
    }

    /// <summary>
    /// Subscribe to global admin streams (SelfHosted only).
    /// In SaaS mode, this is not available and will return an error.
    /// </summary>
    public async Task SubscribeGlobalAdmin()
    {
        if (_deploymentContext.IsSaaS)
        {
            await Clients.Caller.SendAsync("Error", new
            {
                code = "GlobalStreamsNotAvailable",
                message = "Global admin streams are only available in self-hosted mode."
            });
            return;
        }

        var globalStreams = new[]
        {
            "global.all", "global.admin", "global.security", "global.performance"
        };
        foreach (var stream in globalStreams)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, stream);
        }
        _logger.LogInformation("Client {ConnectionId} subscribed to global admin streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedGlobalAdmin", globalStreams);
    }

    /// <summary>
    /// Gets deployment info for the connected client.
    /// </summary>
    public async Task GetDeploymentInfo()
    {
        await Clients.Caller.SendAsync("DeploymentInfo", new
        {
            mode = _deploymentContext.Mode.ToString(),
            isSaaS = _deploymentContext.IsSaaS,
            isSelfHosted = _deploymentContext.IsSelfHosted,
            globalStreamsAvailable = _deploymentContext.IsSelfHosted,
            tenantScoped = _deploymentContext.IsSaaS,
            tenantId = _deploymentContext.IsSaaS ? _tenantContext.TenantId : null
        });
    }

    /// <summary>
    /// In SaaS mode, prefixes stream names with tenant ID for isolation.
    /// In SelfHosted mode, uses global streams.
    /// </summary>
    private string GetEffectiveStreamName(string stream)
    {
        if (_deploymentContext.IsSelfHosted)
        {
            return stream; // Global stream
        }

        // SaaS mode: tenant-scoped streams (use validated tenant from connection items)
        var tenantId = Context.Items["TenantId"] as string ?? _tenantContext.TenantId;
        return $"tenant:{tenantId}:{stream}";
    }

    /// <summary>
    /// Validates an internal JWT token and extracts claims.
    /// </summary>
    private SignalRTokenClaims? ValidateInternalToken(string? token)
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
                ValidIssuer = InternalTokenIssuer,
                ValidateAudience = true,
                ValidAudience = InternalTokenAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

            // Verify token type
            var tokenTypeClaim = principal.FindFirst("token_type")?.Value;
            if (tokenTypeClaim != "internal")
            {
                _logger.LogWarning("SignalR token validation failed: Invalid token_type claim");
                return null;
            }

            return new SignalRTokenClaims
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
            _logger.LogDebug(ex, "SignalR internal token validation failed");
            return null;
        }
    }

    private sealed class SignalRTokenClaims
    {
        public required string TenantId { get; init; }
        public required string UserId { get; init; }
        public string WorkspaceId { get; init; } = "default";
        public required string Role { get; init; }
    }
}
