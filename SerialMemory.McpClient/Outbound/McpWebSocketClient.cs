using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.McpClient.Protocol;

namespace SerialMemory.McpClient.Outbound;

/// <summary>
/// WebSocket client for outbound connection to SerialMemory SaaS backend.
/// Manages connection lifecycle, reconnection, and message routing.
/// </summary>
public sealed class McpWebSocketClient(string serverUrl, string apiKey, ILogger<McpWebSocketClient> logger)
    : IAsyncDisposable
{
    private readonly ConcurrentDictionary<object, TaskCompletionSource<McpEnvelope>> _pendingRequests = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _webSocket;
    private Task? _receiveTask;
    private bool _disposed;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private static readonly TimeSpan[] ReconnectDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public event Action<McpEnvelope>? OnNotification;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await ConnectInternalAsync(cancellationToken);
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("X-API-Key", apiKey);
        _webSocket.Options.SetRequestHeader("User-Agent", "SerialMemory-McpBridge/1.0");
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        logger.LogInformation("Connecting to {Url}", serverUrl);

        try
        {
            await _webSocket.ConnectAsync(new Uri(serverUrl), cancellationToken);
            _reconnectAttempts = 0;
            logger.LogInformation("Connected to backend successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to backend");
            throw;
        }
    }

    public async Task<McpEnvelope> SendRequestAsync(McpEnvelope request, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return McpProtocol.CreateErrorResponse(request.Id, McpError.BackendUnavailable());
        }

        var requestId = request.Id ?? Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<McpEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            await SendEnvelopeAsync(request, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            var registration = timeoutCts.Token.Register(() =>
            {
                tcs.TrySetResult(McpProtocol.CreateErrorResponse(request.Id,
                    McpError.ServerError(-32001, "Request timeout")));
            });

            await using (registration.ConfigureAwait(false))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task SendNotificationAsync(McpEnvelope notification, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            await SendEnvelopeAsync(notification, cancellationToken);
        }
    }

    private async Task SendEnvelopeAsync(McpEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, McpProtocol.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                logger.LogDebug("Sent: {Method} (id={Id})", envelope.Method, envelope.Id);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var messageBuffer = new List<byte>();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                {
                    await ReconnectAsync(cancellationToken);
                    continue;
                }

                var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning("Server closed connection: {Status}", result.CloseStatus);
                    await ReconnectAsync(cancellationToken);
                    continue;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();

                    ProcessMessage(json);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "WebSocket error, attempting reconnect");
                await ReconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Receive loop error");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<McpEnvelope>(json, McpProtocol.JsonOptions);
            if (envelope == null) return;

            logger.LogDebug("Received: {Method} (id={Id})", envelope.Method, envelope.Id);

            // Response to a pending request
            if (envelope.Id != null && _pendingRequests.TryRemove(envelope.Id, out var tcs))
            {
                tcs.TrySetResult(envelope);
                return;
            }

            // Notification from server
            if (envelope.IsNotification)
            {
                OnNotification?.Invoke(envelope);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process message: {Json}", json.Length > 500 ? json[..500] : json);
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            logger.LogError("Max reconnection attempts reached, giving up");

            // Fail all pending requests
            foreach (var (id, tcs) in _pendingRequests)
            {
                tcs.TrySetResult(McpProtocol.CreateErrorResponse(id, McpError.BackendUnavailable()));
            }
            _pendingRequests.Clear();
            return;
        }

        var delay = ReconnectDelays[Math.Min(_reconnectAttempts, ReconnectDelays.Length - 1)];
        logger.LogInformation("Reconnecting in {Delay} (attempt {Attempt}/{Max})",
            delay, _reconnectAttempts + 1, MaxReconnectAttempts);

        await Task.Delay(delay, cancellationToken);

        try
        {
            await ConnectInternalAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reconnection failed");
            _reconnectAttempts++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutdown", default);
            }
            catch { }
        }

        _webSocket?.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }
    }
}
