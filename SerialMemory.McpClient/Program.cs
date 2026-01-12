using Microsoft.Extensions.Logging;
using SerialMemory.McpClient.Bridge;
using SerialMemory.McpClient.Config;

namespace SerialMemory.McpClient;

/// <summary>
/// SerialMemory MCP Bridge - Docker-first dual-mode MCP client.
///
/// Local TCP server on port 4317 → WebSocket bridge to SerialMemory SaaS.
/// Interactive setup on first run, persistent config in /data volume.
/// </summary>
public static class Program
{
    private const int DefaultPort = 4317;
    private const string ConfigPath = "/data/config.json";

    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger("McpBridge");

        logger.LogInformation("SerialMemory MCP Bridge v1.0.0");
        logger.LogInformation("Config path: {Path}", ConfigPath);

        // Load or create configuration
        var configStore = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), ConfigPath);

        if (!await configStore.LoadAsync())
        {
            // First run - need interactive setup
            if (!await configStore.RunInteractiveSetupAsync())
            {
                return 1;
            }
        }

        // Parse port from args or environment
        var port = DefaultPort;
        if (args.Length > 0 && int.TryParse(args[0], out var argPort))
        {
            port = argPort;
        }
        else if (Environment.GetEnvironmentVariable("MCP_PORT") is { } envPort &&
                 int.TryParse(envPort, out var parsedEnvPort))
        {
            port = parsedEnvPort;
        }

        // Create and start bridge
        await using var bridge = new McpBridge(configStore, loggerFactory, port);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            logger.LogInformation("Shutdown requested");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            cts.Cancel();
        };

        try
        {
            await bridge.StartAsync(cts.Token);

            logger.LogInformation("MCP Bridge ready");
            logger.LogInformation("Listening on port {Port}", port);
            logger.LogInformation("Backend: {Url}", configStore.WebSocketUrl);
            logger.LogInformation("Press Ctrl+C to stop");

            // Wait for shutdown
            await WaitForShutdownAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error");
            return 1;
        }

        logger.LogInformation("Goodbye");
        return 0;
    }

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }
}
