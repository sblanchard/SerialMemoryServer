using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Orchestration service for multi-model reasoning.
/// Coordinates multiple models, enforces timeouts, and merges results.
/// </summary>
public interface IMultiModelReasoningService
{
    /// <summary>
    /// Run multi-model reasoning on the knowledge graph.
    /// </summary>
    /// <param name="projectFilter">Optional: filter by project entity name</param>
    /// <param name="maxDurationMs">Maximum total reasoning time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<MultiModelReasoningResult> ReasonAsync(
        string? projectFilter = null,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run multi-model reasoning on entities related to a memory.
    /// </summary>
    /// <param name="memoryId">Memory ID to analyze</param>
    /// <param name="maxDurationMs">Maximum total reasoning time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<MultiModelReasoningResult> ReasonMemoryAsync(
        Guid memoryId,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default);
}
