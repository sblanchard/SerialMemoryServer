using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.McpClient.Protocol;

namespace SerialMemory.McpClient.LocalServer;

/// <summary>
/// TCP server accepting MCP connections from Claude Desktop / Cursor on port 4317.
/// Each connection is handled in its own async task.
/// </summary>
public sealed class McpTcpServer(
    int port,
    Func<McpEnvelope, CancellationToken, Task<McpEnvelope>> requestHandler,
    ILogger<McpTcpServer> logger)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<McpClientConnection> _connections = [];
    private readonly Lock _connectionsLock = new();

    private TcpListener? _listener;
    private Task? _acceptTask;
    private bool _disposed;

    public int ConnectionCount
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections.Count;
            }
        }
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        logger.LogInformation("MCP TCP server listening on port {Port}", port);

        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var connection = new McpClientConnection(tcpClient, requestHandler, logger);

                lock (_connectionsLock)
                {
                    _connections.Add(connection);
                }

                logger.LogInformation("Client connected from {Endpoint}", tcpClient.Client.RemoteEndPoint);

                // Handle connection in background
                _ = HandleConnectionAsync(connection, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleConnectionAsync(McpClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ProcessAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Connection error");
        }
        finally
        {
            lock (_connectionsLock)
            {
                _connections.Remove(connection);
            }
            await connection.DisposeAsync();
            logger.LogInformation("Client disconnected");
        }
    }

    public async Task BroadcastNotificationAsync(McpEnvelope notification)
    {
        List<McpClientConnection> snapshot;
        lock (_connectionsLock)
        {
            snapshot = [.. _connections];
        }

        foreach (var conn in snapshot)
        {
            try
            {
                await conn.SendAsync(notification);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast to client");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        _listener?.Stop();

        List<McpClientConnection> toDispose;
        lock (_connectionsLock)
        {
            toDispose = [.. _connections];
            _connections.Clear();
        }

        foreach (var conn in toDispose)
        {
            await conn.DisposeAsync();
        }

        _cts.Dispose();

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch { }
        }
    }
}

/// <summary>
/// Represents a single client connection to the MCP TCP server.
/// </summary>
internal sealed class McpClientConnection : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Func<McpEnvelope, CancellationToken, Task<McpEnvelope>> _requestHandler;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public McpClientConnection(
        TcpClient tcpClient,
        Func<McpEnvelope, CancellationToken, Task<McpEnvelope>> requestHandler,
        ILogger logger)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        _requestHandler = requestHandler;
        _logger = logger;
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _tcpClient.Connected)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<McpEnvelope>(line, McpProtocol.JsonOptions);
                if (request == null) continue;

                _logger.LogDebug("Received from client: {Method}", request.Method);

                var response = await _requestHandler(request, cancellationToken);
                await SendAsync(response);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON from client");
                var errorResponse = McpProtocol.CreateErrorResponse(null, McpError.ParseError(ex.Message));
                await SendAsync(errorResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing client request");
                var errorResponse = McpProtocol.CreateErrorResponse(null, McpError.InternalError(ex.Message));
                await SendAsync(errorResponse);
            }
        }
    }

    public async Task SendAsync(McpEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, McpProtocol.JsonOptions);

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        _reader.Dispose();
        await _writer.DisposeAsync();
        await _stream.DisposeAsync();
        _tcpClient.Dispose();
    }
}
