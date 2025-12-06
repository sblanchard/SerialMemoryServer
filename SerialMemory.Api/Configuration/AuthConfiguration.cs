using Microsoft.AspNetCore.Authorization;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for authentication and authorization configuration.
/// </summary>
public static class AuthConfiguration
{
    /// <summary>
    /// Adds API key authentication and role-based authorization policies.
    /// </summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services)
    {
        // Authentication - API key handled by middleware
        services.AddAuthentication("ApiKey")
            .AddCookie("ApiKey", options =>
            {
                options.LoginPath = "/api/auth/login";
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });

        // Authorization policies
        services.AddAuthorization(options =>
        {
            // Default policy requires authenticated user
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // Role-based policies
            options.AddPolicy("Admin", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole("admin", "owner"));

            options.AddPolicy("Member", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole("member", "admin", "owner"));

            options.AddPolicy("Owner", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole("owner"));
        });

        return services;
    }

    /// <summary>
    /// Gets JWT configuration from environment.
    /// </summary>
    public static JwtConfig GetJwtConfig(IConfiguration configuration)
    {
        return new JwtConfig
        {
            Secret = configuration["JWT_SECRET"]
                ?? Environment.GetEnvironmentVariable("JWT_SECRET")
                ?? "dev-secret-key-minimum-32-chars!!",
            Issuer = configuration["JWT_ISSUER"]
                ?? Environment.GetEnvironmentVariable("JWT_ISSUER")
                ?? "serialmemory",
            Audience = configuration["JWT_AUDIENCE"]
                ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE")
                ?? "serialmemory-api"
        };
    }
}

/// <summary>
/// JWT configuration values.
/// </summary>
public sealed record JwtConfig
{
    public required string Secret { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
}
