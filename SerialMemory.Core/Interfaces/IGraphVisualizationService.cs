using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for generating graph visualization data with reasoning overlays.
/// </summary>
public interface IGraphVisualizationService
{
    /// <summary>
    /// Generate visualization data for the knowledge graph.
    /// </summary>
    /// <param name="mode">Visualization mode (software, hardware, mixed)</param>
    /// <param name="projectFilter">Optional: filter by project entity name</param>
    /// <param name="includeOverlays">Include reasoning-based overlays</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<GraphVisualizationResult> GenerateVisualizationAsync(
        VisualizationMode mode = VisualizationMode.Mixed,
        string? projectFilter = null,
        bool includeOverlays = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate visualization data for entities related to a memory.
    /// </summary>
    /// <param name="memoryId">Memory ID to visualize</param>
    /// <param name="mode">Visualization mode</param>
    /// <param name="includeOverlays">Include reasoning-based overlays</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<GraphVisualizationResult> GenerateMemoryVisualizationAsync(
        Guid memoryId,
        VisualizationMode mode = VisualizationMode.Mixed,
        bool includeOverlays = true,
        CancellationToken cancellationToken = default);
}
