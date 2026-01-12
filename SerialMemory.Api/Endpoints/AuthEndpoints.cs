namespace SerialMemory.Api.Endpoints;

/// <summary>
/// Auth endpoints for the main API.
/// Note: Signup, verify-email, and magic-link endpoints are now in Dashboard/DashboardExtensions.cs
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // GET /api/auth/whoami - Get current user info (requires auth)
        app.MapGet("/api/auth/whoami", (
            HttpContext _,
            Core.Interfaces.ITenantContext tenantContext) =>
        {
            if (string.IsNullOrEmpty(tenantContext.TenantId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                tenantId = tenantContext.TenantId,
                userId = tenantContext.UserId,
                workspaceId = tenantContext.WorkspaceId,
                role = tenantContext.UserRole ?? "user",
                isLabMode = tenantContext.IsLabMode,
                isRootAdmin = tenantContext.IsRootAdmin,
                scopes = tenantContext.Scopes
            });
        })
        .WithName("WhoAmI")
        .WithDescription("Get current authenticated user info");

        // Backwards compatibility aliases - redirect to new endpoints
        app.MapPost("/api/auth/register", () => Results.Redirect("/signup", permanent: true))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost("/api/auth/signup", () => Results.Redirect("/signup", permanent: true))
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
