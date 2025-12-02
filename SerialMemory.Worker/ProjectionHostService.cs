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
    private const int MaxRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting ProjectionHostService");

        var retryCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await projectionHost.StartAsync(stoppingToken);

                // Reset retry count on successful start
                retryCount = 0;

                // Keep the service running until cancellation
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("ProjectionHostService stopping");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                logger.LogError(ex, "ProjectionHostService error (attempt {Attempt}/{MaxRetries})", retryCount, MaxRetries);

                if (retryCount >= MaxRetries)
                {
                    logger.LogCritical("ProjectionHostService failed after {MaxRetries} attempts, entering degraded mode", MaxRetries);
                    // Don't throw - let the worker continue running other services
                    // Just wait indefinitely until shutdown
                    try
                    {
                        await Task.Delay(Timeout.Infinite, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Wait before retrying
                try
                {
                    await Task.Delay(RetryDelay * retryCount, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        try
        {
            await projectionHost.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error stopping ProjectionHost");
        }
    }
}
