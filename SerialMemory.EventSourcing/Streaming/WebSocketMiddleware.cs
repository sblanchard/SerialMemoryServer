using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SerialMemory.EventSourcing.Streaming;

/// <summary>
/// ASP.NET Core middleware for handling WebSocket connections to the event stream.
/// </summary>
public sealed class WebSocketEventMiddleware(
    RequestDelegate next,
    WebSocketEventHub eventHub,
    ILogger<WebSocketEventMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/ws/events")
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketAsync(webSocket, context.RequestAborted);
            }
            else
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
            }
        }
        else
        {
            await next(context);
        }
    }

    private async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var connectionId = eventHub.RegisterConnection(webSocket);

        logger.LogInformation("WebSocket client connected: {ConnectionId}", connectionId);

        try
        {
            // Send initial connection acknowledgment
            await eventHub.SendToConnectionAsync(
                connectionId,
                $"{{\"type\":\"connected\",\"connectionId\":\"{connectionId}\"}}",
                cancellationToken);

            // Handle incoming messages until disconnection
            await eventHub.HandleMessagesAsync(connectionId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WebSocket connection {ConnectionId}", connectionId);
        }
        finally
        {
            await eventHub.UnregisterConnectionAsync(connectionId);
            logger.LogInformation("WebSocket client disconnected: {ConnectionId}", connectionId);
        }
    }
}

/// <summary>
/// Extension methods for WebSocket event streaming.
/// </summary>
public static class WebSocketEventExtensions
{
    /// <summary>
    /// Add WebSocket event hub services.
    /// </summary>
    public static IServiceCollection AddWebSocketEventStreaming(this IServiceCollection services)
    {
        services.AddSingleton<WebSocketEventHub>();
        return services;
    }

    /// <summary>
    /// Use WebSocket event streaming middleware.
    /// </summary>
    public static IApplicationBuilder UseWebSocketEventStreaming(this IApplicationBuilder app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        app.UseMiddleware<WebSocketEventMiddleware>();

        return app;
    }
}

