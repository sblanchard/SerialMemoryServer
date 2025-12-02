using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Control Room endpoints for kill switch and quota management.
/// Admin-only access for ops team.
/// </summary>
public static class ControlRoomEndpoints
{
    public static void MapControlRoomEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/admin/control-room")
            .WithTags("Control Room")
            .RequireAuthorization("Owner");

        // =============================================================================
        // GET /admin/control-room/state - Get full dashboard state
        // =============================================================================
        group.MapGet("/state", async (
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var state = await killSwitchService.GetDashboardStateAsync(ct);
            return Results.Ok(state);
        })
        .WithName("GetControlRoomState")
        .WithDescription("Gets the full control room state including global and tenant kill switches")
        .Produces<KillSwitchDashboardState>(StatusCodes.Status200OK);

        // =============================================================================
        // GET /admin/control-room/actions - Get recent kill switch actions
        // =============================================================================
        group.MapGet("/actions", async (
            int? limit,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var actions = await killSwitchService.GetRecentActionsAsync(limit ?? 50, ct);
            return Results.Ok(actions);
        })
        .WithName("GetControlRoomActions")
        .WithDescription("Gets recent kill switch actions for audit trail")
        .Produces<IReadOnlyList<KillSwitchAction>>(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/global/kill - Enable global kill switch
        // =============================================================================
        group.MapPost("/global/kill", async (
            [FromBody] GlobalKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            TimeSpan? duration = request.ExpiresInMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ExpiresInMinutes.Value)
                : null;

            await killSwitchService.EnableGlobalKillSwitchAsync(
                request.Reason,
                userId,
                duration,
                ct);

            return Results.Ok(new { success = true, message = "Global kill switch enabled" });
        })
        .WithName("EnableGlobalKillSwitch")
        .WithDescription("Enables the global kill switch - blocks ALL requests (503)")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // DELETE /admin/control-room/global/kill - Disable global kill switch
        // =============================================================================
        group.MapDelete("/global/kill", async (
            [FromBody] LiftKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableGlobalKillSwitchAsync(request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = "Global kill switch disabled" });
        })
        .WithName("DisableGlobalKillSwitch")
        .WithDescription("Disables the global kill switch")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/global/readonly - Enable global read-only mode
        // =============================================================================
        group.MapPost("/global/readonly", async (
            [FromBody] GlobalKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            TimeSpan? duration = request.ExpiresInMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ExpiresInMinutes.Value)
                : null;

            await killSwitchService.EnableGlobalReadOnlyAsync(
                request.Reason,
                userId,
                duration,
                ct);

            return Results.Ok(new { success = true, message = "Global read-only mode enabled" });
        })
        .WithName("EnableGlobalReadOnly")
        .WithDescription("Enables global read-only mode - allows GET, blocks write verbs")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // DELETE /admin/control-room/global/readonly - Disable global read-only mode
        // =============================================================================
        group.MapDelete("/global/readonly", async (
            [FromBody] LiftKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableGlobalReadOnlyAsync(request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = "Global read-only mode disabled" });
        })
        .WithName("DisableGlobalReadOnly")
        .WithDescription("Disables global read-only mode")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/tenant/{tenantId}/kill - Enable tenant kill switch
        // =============================================================================
        group.MapPost("/tenant/{tenantId}/kill", async (
            string tenantId,
            [FromBody] TenantKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            TimeSpan? duration = request.ExpiresInMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ExpiresInMinutes.Value)
                : null;

            await killSwitchService.EnableTenantKillSwitchAsync(
                tenantId,
                request.Reason,
                userId,
                duration,
                ct);

            return Results.Ok(new { success = true, message = $"Tenant {tenantId} disabled" });
        })
        .WithName("EnableTenantKillSwitch")
        .WithDescription("Disables all operations for a specific tenant")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // DELETE /admin/control-room/tenant/{tenantId}/kill - Disable tenant kill switch
        // =============================================================================
        group.MapDelete("/tenant/{tenantId}/kill", async (
            string tenantId,
            [FromBody] LiftKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableTenantKillSwitchAsync(tenantId, request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = $"Tenant {tenantId} re-enabled" });
        })
        .WithName("DisableTenantKillSwitch")
        .WithDescription("Re-enables a disabled tenant")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/tenant/{tenantId}/readonly - Enable tenant read-only
        // =============================================================================
        group.MapPost("/tenant/{tenantId}/readonly", async (
            string tenantId,
            [FromBody] TenantKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            TimeSpan? duration = request.ExpiresInMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ExpiresInMinutes.Value)
                : null;

            await killSwitchService.EnableTenantReadOnlyAsync(
                tenantId,
                request.Reason,
                userId,
                duration,
                ct);

            return Results.Ok(new { success = true, message = $"Tenant {tenantId} set to read-only" });
        })
        .WithName("EnableTenantReadOnly")
        .WithDescription("Sets a tenant to read-only mode")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // DELETE /admin/control-room/tenant/{tenantId}/readonly - Disable tenant read-only
        // =============================================================================
        group.MapDelete("/tenant/{tenantId}/readonly", async (
            string tenantId,
            [FromBody] LiftKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableTenantReadOnlyAsync(tenantId, request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = $"Tenant {tenantId} read-only mode disabled" });
        })
        .WithName("DisableTenantReadOnly")
        .WithDescription("Disables read-only mode for a tenant")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/tenant/{tenantId}/no-ingest - Enable no-ingest mode
        // =============================================================================
        group.MapPost("/tenant/{tenantId}/no-ingest", async (
            string tenantId,
            [FromBody] TenantKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            TimeSpan? duration = request.ExpiresInMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ExpiresInMinutes.Value)
                : null;

            await killSwitchService.EnableTenantNoIngestAsync(
                tenantId,
                request.Reason,
                userId,
                duration,
                ct);

            return Results.Ok(new { success = true, message = $"Tenant {tenantId} memory ingestion disabled" });
        })
        .WithName("EnableTenantNoIngest")
        .WithDescription("Disables memory ingestion for a tenant (searches still work)")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // DELETE /admin/control-room/tenant/{tenantId}/no-ingest - Disable no-ingest mode
        // =============================================================================
        group.MapDelete("/tenant/{tenantId}/no-ingest", async (
            string tenantId,
            [FromBody] LiftKillSwitchRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableTenantNoIngestAsync(tenantId, request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = $"Tenant {tenantId} memory ingestion re-enabled" });
        })
        .WithName("DisableTenantNoIngest")
        .WithDescription("Re-enables memory ingestion for a tenant")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/api-key/{keyId}/disable - Disable API key
        // =============================================================================
        group.MapPost("/api-key/{keyId}/disable", async (
            string keyId,
            [FromBody] ApiKeyActionRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.DisableApiKeyAsync(keyId, request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = $"API key {keyId} disabled" });
        })
        .WithName("DisableApiKey")
        .WithDescription("Disables a specific API key")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/control-room/api-key/{keyId}/enable - Enable API key
        // =============================================================================
        group.MapPost("/api-key/{keyId}/enable", async (
            string keyId,
            [FromBody] ApiKeyActionRequest request,
            ClaimsPrincipal user,
            IKillSwitchService killSwitchService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            await killSwitchService.EnableApiKeyAsync(keyId, request.Reason, userId, ct);
            return Results.Ok(new { success = true, message = $"API key {keyId} enabled" });
        })
        .WithName("EnableApiKey")
        .WithDescription("Re-enables a disabled API key")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /admin/control-room/tenant/{tenantId}/quota - Get tenant quota status
        // =============================================================================
        group.MapGet("/tenant/{tenantId}/quota", async (
            string tenantId,
            string? workspaceId,
            [FromServices] IQuotaEnforcementService quotaService,
            CancellationToken ct) =>
        {
            var status = await quotaService.GetStatusAsync(tenantId, workspaceId ?? "default", ct);
            return Results.Ok(status);
        })
        .WithName("GetTenantQuotaStatus")
        .WithDescription("Gets current quota status for a tenant")
        .Produces<QuotaStatus>(StatusCodes.Status200OK);

        // =============================================================================
        // GET /admin/control-room/search/tenant - Search for tenant
        // =============================================================================
        group.MapGet("/search/tenant", async (
            string? query,
            int? limit,
            IAdminService adminService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "validation_error", message = "Query is required" });

            var result = await adminService.SearchTenantsAsync(query, limit ?? 20, ct);
            return Results.Ok(result);
        })
        .WithName("SearchTenants")
        .WithDescription("Search for tenants by name, slug, or email")
        .Produces<IReadOnlyList<TenantSearchResult>>(StatusCodes.Status200OK);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// Request DTOs for Control Room
public sealed record GlobalKillSwitchRequest(string Reason, int? ExpiresInMinutes = null);
public sealed record TenantKillSwitchRequest(string Reason, int? ExpiresInMinutes = null);
public sealed record LiftKillSwitchRequest(string Reason);
public sealed record ApiKeyActionRequest(string Reason);
