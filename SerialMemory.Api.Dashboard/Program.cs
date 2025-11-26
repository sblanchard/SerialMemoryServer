using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var postgresHost = builder.Configuration["POSTGRES_HOST"] ?? "localhost";
var postgresPort = builder.Configuration["POSTGRES_PORT"] ?? "5432";
var postgresUser = builder.Configuration["POSTGRES_USER"] ?? "postgres";
var postgresPassword = builder.Configuration["POSTGRES_PASSWORD"] ?? "postgres";
var postgresDb = builder.Configuration["POSTGRES_DB"] ?? "contextdb";
var connectionString = $"Host={postgresHost};Port={postgresPort};Database={postgresDb};Username={postgresUser};Password={postgresPassword}";

var jwtSecret = builder.Configuration["JWT_SECRET"] ?? "default-development-secret-32chars!!";
var jwtIssuer = builder.Configuration["JWT_ISSUER"] ?? "serialmemory";
var jwtAudience = builder.Configuration["JWT_AUDIENCE"] ?? "serialmemory-api";
var selfHostedMode = builder.Configuration["SERIALMEMORY_MODE"]?.ToLowerInvariant() == "self-hosted";

// Add services
builder.Services.AddSingleton<ITenantDashboardService>(sp =>
    new TenantDashboardService(connectionString, sp.GetRequiredService<ILogger<TenantDashboardService>>()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Owner", policy => policy.RequireClaim("role", "owner"));
    options.AddPolicy("Admin", policy => policy.RequireClaim("role", "owner", "admin"));
    options.AddPolicy("Member", policy => policy.RequireClaim("role", "owner", "admin", "member"));
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// Helper to extract tenant info from claims
static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
{
    if (selfHosted)
    {
        return (Guid.Parse("00000000-0000-0000-0000-000000000000"), "self-hosted", "default");
    }

    var tenantIdClaim = user.FindFirst("tenant_id")?.Value
        ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

    if (!Guid.TryParse(tenantIdClaim, out var tenantId))
        throw new UnauthorizedAccessException("Invalid tenant_id claim");

    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("Missing user identifier");

    var workspaceId = user.FindFirst("workspace_id")?.Value ?? "default";

    return (tenantId, userId, workspaceId);
}

// =============================================================================
// GET /me - Current user info
// =============================================================================
app.MapGet("/me", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetCurrentUserAsync(tenantId, userId, ct);
    return Results.Ok(result);
})
.WithName("GetCurrentUser")
.WithDescription("Gets current user information within the tenant context")
.RequireAuthorization()
.Produces<UserInfoResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// GET /tenant/usage - Current cycle usage
// =============================================================================
app.MapGet("/tenant/usage", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, workspaceId) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetTenantUsageAsync(tenantId, workspaceId, ct);
    return Results.Ok(result);
})
.WithName("GetTenantUsage")
.WithDescription("Gets current billing cycle usage statistics")
.RequireAuthorization()
.Produces<TenantUsageResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// GET /tenant/plan - Plan details
// =============================================================================
app.MapGet("/tenant/plan", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetTenantPlanAsync(tenantId, ct);
    return Results.Ok(result);
})
.WithName("GetTenantPlan")
.WithDescription("Gets tenant plan details and current limits status")
.RequireAuthorization()
.Produces<TenantPlanResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// POST /tenant/export - Request data export
// =============================================================================
app.MapPost("/tenant/export", async (
    ClaimsPrincipal user,
    ExportRequestOptions? options,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
    options ??= new ExportRequestOptions();

    try
    {
        var result = await dashboardService.RequestExportAsync(tenantId, userId, options, ct);
        return Results.Accepted($"/tenant/export/{result.ExportId}", result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = "export_in_progress", message = ex.Message });
    }
})
.WithName("RequestExport")
.WithDescription("Requests a data export for the tenant")
.RequireAuthorization("Member")
.Produces<ExportRequestResult>(StatusCodes.Status202Accepted)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status409Conflict);

// =============================================================================
// GET /tenant/export/{id} - Export status
// =============================================================================
app.MapGet("/tenant/export/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetExportStatusAsync(tenantId, id, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "Export request not found" });

    return Results.Ok(result);
})
.WithName("GetExportStatus")
.WithDescription("Gets the status of an export request")
.RequireAuthorization()
.Produces<ExportStatusResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// DELETE /tenant - Request tenant deletion (owner only)
// =============================================================================
app.MapDelete("/tenant", async (
    ClaimsPrincipal user,
    DeleteTenantRequest request,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Deletion not allowed in self-hosted mode" });

    try
    {
        var result = await dashboardService.RequestDeletionAsync(tenantId, userId, request.ConfirmationPhrase, ct);
        return Results.Accepted($"/tenant/deletion", result);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = "invalid_confirmation", message = ex.Message });
    }
})
.WithName("RequestDeletion")
.WithDescription("Requests tenant deletion (owner only, 30-day grace period)")
.RequireAuthorization("Owner")
.Produces<DeletionRequestResult>(StatusCodes.Status202Accepted)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

// =============================================================================
// GET /tenant/deletion - Deletion status
// =============================================================================
app.MapGet("/tenant/deletion", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetDeletionStatusAsync(tenantId, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "No deletion request found" });

    return Results.Ok(result);
})
.WithName("GetDeletionStatus")
.WithDescription("Gets the status of a deletion request")
.RequireAuthorization()
.Produces<DeletionStatusResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// POST /tenant/deletion/cancel - Cancel deletion
// =============================================================================
app.MapPost("/tenant/deletion/cancel", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

    try
    {
        var cancelled = await dashboardService.CancelDeletionAsync(tenantId, userId, ct);

        if (!cancelled)
            return Results.NotFound(new { error = "not_found", message = "No pending deletion request found" });

        return Results.Ok(new { message = "Deletion cancelled successfully" });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
})
.WithName("CancelDeletion")
.WithDescription("Cancels a pending deletion request")
.RequireAuthorization("Admin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// Health check
// =============================================================================
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
.WithName("HealthCheck")
.ExcludeFromDescription();

app.Run();

// Request DTOs
public sealed record DeleteTenantRequest(string ConfirmationPhrase);
