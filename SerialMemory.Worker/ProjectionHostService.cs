using Microsoft.Extensions.Hosting;
using SerialMemory.EventSourcing.Projections;

namespace SerialMemory.Worker;

/// <summary>
/// BackgroundService wrapper for ProjectionHost.
/// Bridges between IHostedService (for DI) and IProjectionHost.
/// </summary>
public sealed class ProjectionHostService(
    IProjectionHost projectionHost,
    ILogger<ProjectionHostService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting ProjectionHostService");

        try
        {
            await projectionHost.StartAsync(stoppingToken);

            // Keep the service running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("ProjectionHostService stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProjectionHostService error");
            throw;
        }
        finally
        {
            await projectionHost.StopAsync(CancellationToken.None);
        }
    }
}
