using Microsoft.Extensions.Hosting;
using SerialMemory.EventSourcing.Projections;

namespace SerialMemory.Worker;

/// <summary>
/// BackgroundService wrapper for ProjectionHost.
/// Bridges between IHostedService (for DI) and IProjectionHost.
/// </summary>
public sealed class ProjectionHostService : BackgroundService
{
    private readonly IProjectionHost _projectionHost;
    private readonly ILogger<ProjectionHostService> _logger;

    public ProjectionHostService(
        IProjectionHost projectionHost,
        ILogger<ProjectionHostService> logger)
    {
        _projectionHost = projectionHost;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting ProjectionHostService");

        try
        {
            await _projectionHost.StartAsync(stoppingToken);

            // Keep the service running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("ProjectionHostService stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProjectionHostService error");
            throw;
        }
        finally
        {
            await _projectionHost.StopAsync(CancellationToken.None);
        }
    }
}
