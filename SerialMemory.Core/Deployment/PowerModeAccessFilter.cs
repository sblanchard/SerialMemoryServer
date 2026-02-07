using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Deployment;

/// <summary>
/// Attribute to mark endpoints that require root admin access.
/// Only the root admin (SERIALMEMORY_ROOT_ADMIN_EMAIL) can access these endpoints.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RootAdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var tenantContext = context.HttpContext.RequestServices.GetService<ITenantContext>();

        if (tenantContext?.IsRootAdmin != true)
        {
            context.Result = new ObjectResult(new
            {
                error = "RootAdminRequired",
                message = "This endpoint is only available to the root administrator."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Attribute to mark endpoints that require power mode access.
/// Root admins bypass all checks. In SelfHosted, owners get access. In SaaS, lab mode + AllowPowerMode required.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresPowerModeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var deploymentContext = context.HttpContext.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.HttpContext.RequestServices.GetService<ITenantContext>();

        // Root admin bypasses all power mode restrictions
        if (tenantContext?.IsRootAdmin == true)
        {
            return;
        }

        // Check if power mode is globally disabled
        if (deploymentContext?.PowerModeGloballyDisabled == true)
        {
            context.Result = new ObjectResult(new
            {
                error = "PowerModeDisabled",
                message = "Power mode has been globally disabled by the administrator."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        // In SelfHosted mode, allow all access
        if (deploymentContext?.IsSelfHosted == true)
        {
            // Self-hosted mode: full access for everyone
            return;
        }

        // In SaaS mode, check tenant permissions
        if (tenantContext == null)
        {
            context.Result = new ObjectResult(new
            {
                error = "Unauthorized",
                message = "Tenant context is required."
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        // In SaaS mode: only lab mode tenants with AllowPowerMode can access
        if (!tenantContext.IsLabMode)
        {
            context.Result = new ObjectResult(new
            {
                error = "LabModeRequired",
                message = "Power mode endpoints are only available to lab mode tenants. Contact support to enable lab mode."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        if (!tenantContext.AllowPowerMode)
        {
            context.Result = new ObjectResult(new
            {
                error = "PowerModeNotEnabled",
                message = "Power mode is not enabled for this tenant. Contact support to enable power mode access."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Attribute to mark endpoints only available in self-hosted mode.
/// Root admin can access these in SaaS mode for administration purposes.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class SelfHostedOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var deploymentContext = context.HttpContext.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.HttpContext.RequestServices.GetService<ITenantContext>();

        // Root admin can access self-hosted endpoints in SaaS mode
        if (tenantContext?.IsRootAdmin == true)
        {
            return;
        }

        if (deploymentContext?.IsSelfHosted != true)
        {
            context.Result = new ObjectResult(new
            {
                error = "SelfHostedOnly",
                message = "This endpoint is only available in self-hosted deployments."
            })
            {
                StatusCode = StatusCodes.Status404NotFound
            };
            return;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Middleware to check power mode and admin access for specific route patterns.
/// Use this for minimal API endpoints.
/// </summary>
public sealed class PowerModeAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string[] _powerModePathPrefixes =
    [
        "/api/power/",
        "/api/mutations/"
    ];

    private readonly string[] _selfHostedOnlyPrefixes =
    [
        "/api/selfhost/",
        "/api/admin/config/"
    ];

    private readonly string[] _rootAdminOnlyPrefixes =
    [
        "/api/root/",
        "/api/admin/tenants/"
    ];

    public PowerModeAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        var deploymentContext = context.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.RequestServices.GetService<ITenantContext>();

        // Check root admin only paths
        var isRootAdminPath = _rootAdminOnlyPrefixes.Any(prefix => path.StartsWith(prefix));
        if (isRootAdminPath)
        {
            if (tenantContext?.IsRootAdmin != true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "RootAdminRequired",
                    message = "This endpoint is only available to the root administrator."
                });
                return;
            }
        }

        // Check self-hosted only paths
        var isSelfHostedPath = _selfHostedOnlyPrefixes.Any(prefix => path.StartsWith(prefix));
        if (isSelfHostedPath)
        {
            // Root admin can access self-hosted paths in SaaS mode
            if (tenantContext?.IsRootAdmin != true && deploymentContext?.IsSelfHosted != true)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "SelfHostedOnly",
                    message = "This endpoint is only available in self-hosted deployments."
                });
                return;
            }
        }

        // Check if this is a power mode path
        var isPowerModePath = _powerModePathPrefixes.Any(prefix => path.StartsWith(prefix));

        if (isPowerModePath)
        {
            // DEBUG: Log power mode access check
            var logger = context.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<PowerModeAccessMiddleware>>();
            logger?.LogInformation(
                "POWER_MODE_CHECK: path={Path}, tenantContext.IsRootAdmin={IsRootAdmin}, " +
                "tenantContext.IsLabMode={IsLabMode}, tenantContext.AllowPowerMode={AllowPowerMode}, " +
                "deploymentContext.IsSelfHosted={IsSelfHosted}",
                path,
                tenantContext?.IsRootAdmin,
                tenantContext?.IsLabMode,
                tenantContext?.AllowPowerMode,
                deploymentContext?.IsSelfHosted);

            // Root admin bypasses all power mode restrictions
            if (tenantContext?.IsRootAdmin == true)
            {
                logger?.LogInformation("POWER_MODE_ACCESS: Granted (root admin) for {Path}", path);
                await _next(context);
                return;
            }

            // Check if power mode is globally disabled
            if (deploymentContext?.PowerModeGloballyDisabled == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "PowerModeDisabled",
                    message = "Power mode has been globally disabled by the administrator."
                });
                return;
            }

            // In SelfHosted mode, allow all access
            if (deploymentContext?.IsSelfHosted == true)
            {
                // Self-hosted mode: full access for everyone
                await _next(context);
                return;
            }
            else
            {
                // SaaS mode checks
                if (tenantContext == null || !tenantContext.IsLabMode || !tenantContext.AllowPowerMode)
                {
                    var saasLogger = context.RequestServices.GetService<ILogger<PowerModeAccessMiddleware>>();
                    saasLogger?.LogWarning(
                        "POWER_MODE_DENIED (SaaS mode): path={Path}, tenantContext={TenantContext}, " +
                        "IsLabMode={IsLabMode}, AllowPowerMode={AllowPowerMode}, IsRootAdmin={IsRootAdmin}",
                        path,
                        tenantContext != null ? "set" : "null",
                        tenantContext?.IsLabMode,
                        tenantContext?.AllowPowerMode,
                        tenantContext?.IsRootAdmin);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "PowerModeAccessDenied",
                        message = "Power mode endpoints require lab mode and explicit power mode permission. Contact support to enable."
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}

/// <summary>
/// Extension methods for power mode and admin access.
/// </summary>
public static class PowerModeAccessExtensions
{
    /// <summary>
    /// Adds power mode access middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UsePowerModeAccessMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<PowerModeAccessMiddleware>();
    }

    /// <summary>
    /// Checks if the current context allows power mode access.
    /// </summary>
    public static bool CanAccessPowerMode(this HttpContext context)
    {
        var deploymentContext = context.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.RequestServices.GetService<ITenantContext>();

        // Root admin always has access
        if (tenantContext?.IsRootAdmin == true)
            return true;

        if (deploymentContext?.PowerModeGloballyDisabled == true)
            return false;

        // SelfHosted: only owners
        if (deploymentContext?.IsSelfHosted == true)
            return tenantContext?.IsOwner == true;

        // SaaS: lab mode + AllowPowerMode
        return tenantContext?.IsLabMode == true && tenantContext.AllowPowerMode;
    }

    /// <summary>
    /// Checks if the current context can access self-hosted admin features.
    /// </summary>
    public static bool CanAccessSelfHostedAdmin(this HttpContext context)
    {
        var deploymentContext = context.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.RequestServices.GetService<ITenantContext>();

        // Root admin can access in any mode
        if (tenantContext?.IsRootAdmin == true)
            return true;

        // Otherwise, must be self-hosted mode
        return deploymentContext?.IsSelfHosted == true;
    }

    /// <summary>
    /// Checks if the current user is the root administrator.
    /// Works for both API (via ITenantContext) and web apps (via cookie claims).
    /// </summary>
    public static bool IsRootAdmin(this HttpContext context)
    {
        // First check ITenantContext (API auth)
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext?.IsRootAdmin == true)
            return true;

        // Fallback: check cookie claims (web app auth)
        var isRootAdminClaim = context.User?.FindFirst("is_root_admin")?.Value;
        return string.Equals(isRootAdminClaim, "true", StringComparison.OrdinalIgnoreCase);
    }
}
