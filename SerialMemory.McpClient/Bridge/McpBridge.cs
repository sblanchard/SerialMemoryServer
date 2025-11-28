using Microsoft.Extensions.Logging;
using SerialMemory.McpClient.Config;
using SerialMemory.McpClient.LocalServer;
using SerialMemory.McpClient.Outbound;
using SerialMemory.McpClient.Protocol;

namespace SerialMemory.McpClient.Bridge;

/// <summary>
/// Bridges local MCP TCP connections to the remote SerialMemory WebSocket backend.
/// Handles request routing, failsafe error handling, and connection management.
/// </summary>
public sealed class McpBridge(ConfigStore configStore, ILoggerFactory loggerFactory, int localPort = 4317)
    : IAsyncDisposable
{
    private readonly ILogger<McpBridge> _logger = loggerFactory.CreateLogger<McpBridge>();

    private McpTcpServer? _tcpServer;
    private McpWebSocketClient? _wsClient;
    private bool _disposed;

    public bool IsRunning => _tcpServer != null;
    public int ConnectionCount => _tcpServer?.ConnectionCount ?? 0;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!configStore.IsConfigured)
            throw new InvalidOperationException("Configuration not loaded");

        // Connect to backend first
        _wsClient = new McpWebSocketClient(
            configStore.WebSocketUrl,
            configStore.GetApiKey(),
            loggerFactory.CreateLogger<McpWebSocketClient>()
        );

        _wsClient.OnNotification += HandleBackendNotification;

        try
        {
            await _wsClient.ConnectAsync(cancellationToken);
            _logger.LogInformation("Connected to backend: {Url}", configStore.WebSocketUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to backend - will retry on requests");
        }

        // Start local TCP server
        _tcpServer = new McpTcpServer(
            localPort,
            HandleClientRequestAsync,
            loggerFactory.CreateLogger<McpTcpServer>()
        );

        _tcpServer.Start();
        _logger.LogInformation("MCP Bridge running on port {Port}", localPort);
    }

    private async Task<McpEnvelope> HandleClientRequestAsync(McpEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                // Handle local-only requests
                case "initialize":
                    return McpProtocol.CreateInitializeResponse(request.Id);
                case "notifications/initialized":
                    // Notification - no response needed
                    return new McpEnvelope { Id = request.Id, Result = new { } };
            }

            // Forward to backend
            if (_wsClient is not { IsConnected: true })
            {
                _logger.LogWarning("Backend unavailable for request: {Method}", request.Method);
                return McpProtocol.CreateErrorResponse(request.Id, McpError.BackendUnavailable());
            }

            var response = await _wsClient.SendRequestAsync(request, cancellationToken);

            // Ensure response has correct ID
            if (response.Id == null && request.Id != null)
            {
                return new McpEnvelope
                {
                    Id = request.Id,
                    Result = response.Result,
                    Error = response.Error
                };
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            return McpProtocol.CreateErrorResponse(request.Id,
                McpError.ServerError(-32001, "Request cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling request: {Method}", request.Method);
            return McpProtocol.CreateErrorResponse(request.Id,
                McpError.InternalError($"Bridge error: {ex.Message}"));
        }
    }

    private void HandleBackendNotification(McpEnvelope notification)
    {
        // Forward notifications from backend to all connected clients
        if (_tcpServer != null)
        {
            _ = _tcpServer.BroadcastNotificationAsync(notification);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_tcpServer != null)
        {
            await _tcpServer.DisposeAsync();
        }

        if (_wsClient != null)
        {
            await _wsClient.DisposeAsync();
        }

        _logger.LogInformation("MCP Bridge stopped");
    }
}
