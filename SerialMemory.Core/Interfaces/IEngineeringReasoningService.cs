using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Engineering reasoning service for inferring risks, conflicts, and insights
/// from the knowledge graph.
/// </summary>
public interface IEngineeringReasoningService
{
    /// <summary>
    /// Analyzes the knowledge graph for engineering insights.
    /// Performs all rule-based inference checks.
    /// </summary>
    /// <param name="projectFilter">Optional: filter by project entity name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Analysis result with detected insights</returns>
    Task<EngineeringAnalysisResult> AnalyzeAsync(
        string? projectFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes entities related to a specific memory.
    /// </summary>
    /// <param name="memoryId">Memory ID to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Analysis result with detected insights</returns>
    Task<EngineeringAnalysisResult> AnalyzeMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects power integrity issues (voltage mismatch, overcurrent, undervoltage chains).
    /// </summary>
    Task<List<EngineeringInsight>> DetectPowerIntegrityIssuesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects signal integrity issues (clock mismatch, protocol mismatch, baud rate issues).
    /// </summary>
    Task<List<EngineeringInsight>> DetectSignalIntegrityIssuesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects dependency corruption (service A depends on service B which has incident).
    /// </summary>
    Task<List<EngineeringInsight>> DetectDependencyCorruptionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects thermal risk (temperature exceeds component ratings).
    /// </summary>
    Task<List<EngineeringInsight>> DetectThermalRiskAsync(
        CancellationToken cancellationToken = default);
}
