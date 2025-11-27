using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Deployment;

/// <summary>
/// Attribute to mark endpoints that require power mode access.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresPowerModeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var deploymentContext = context.HttpContext.RequestServices.GetService<IDeploymentContext>();
        var tenantContext = context.HttpContext.RequestServices.GetService<ITenantContext>();

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

        // In SelfHosted mode, power mode is allowed by default
        if (deploymentContext?.IsSelfHosted == true)
        {
            // Power mode allowed unless globally disabled (checked above)
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
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class SelfHostedOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var deploymentContext = context.HttpContext.RequestServices.GetService<IDeploymentContext>();

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
/// Middleware to check power mode access for specific route patterns.
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

    public PowerModeAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Check if this is a power mode path
        var isPowerModePath = _powerModePathPrefixes.Any(prefix => path.StartsWith(prefix));

        if (isPowerModePath)
        {
            var deploymentContext = context.RequestServices.GetService<IDeploymentContext>();
            var tenantContext = context.RequestServices.GetService<ITenantContext>();

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

            // In SelfHosted mode, power mode is allowed by default
            if (deploymentContext?.IsSelfHosted != true)
            {
                // SaaS mode checks
                if (tenantContext == null || !tenantContext.IsLabMode || !tenantContext.AllowPowerMode)
                {
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
/// Extension methods for power mode access.
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

        if (deploymentContext?.PowerModeGloballyDisabled == true)
            return false;

        if (deploymentContext?.IsSelfHosted == true)
            return true;

        return tenantContext?.IsLabMode == true && tenantContext.AllowPowerMode;
    }
}
