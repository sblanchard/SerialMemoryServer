using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for orchestrating and persisting reasoning executions.
/// Calls MultiModelReasoningService and persists results to database.
/// </summary>
public interface IReasoningRunService
{
    /// <summary>
    /// Start a reasoning execution for a specific memory.
    /// Creates execution record, runs reasoning, persists results.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="userId">User ID</param>
    /// <param name="memoryId">Memory ID to analyze</param>
    /// <param name="maxDurationMs">Maximum execution time in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Execution ID for tracking</returns>
    Task<Guid> RunByMemoryAsync(
        Guid tenantId,
        string userId,
        Guid memoryId,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a reasoning execution for a text query.
    /// Creates execution record, runs reasoning, persists results.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="userId">User ID</param>
    /// <param name="query">Query text to analyze</param>
    /// <param name="maxDurationMs">Maximum execution time in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Execution ID for tracking</returns>
    Task<Guid> RunByQueryAsync(
        Guid tenantId,
        string userId,
        string query,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a reasoning execution for a project filter.
    /// Creates execution record, runs reasoning, persists results.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="userId">User ID</param>
    /// <param name="projectName">Project name to filter by</param>
    /// <param name="maxDurationMs">Maximum execution time in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Execution ID for tracking</returns>
    Task<Guid> RunByProjectAsync(
        Guid tenantId,
        string userId,
        string? projectName,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a reasoning execution by ID with full details.
    /// Includes traces, insights, and disagreements.
    /// </summary>
    /// <param name="executionId">Execution ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full execution details or null if not found</returns>
    Task<ReasoningExecution?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent reasoning executions for a tenant.
    /// Returns lightweight summaries without full details.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="limit">Maximum number of executions to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of execution summaries</returns>
    Task<List<ReasoningExecutionSummary>> GetRecentExecutionsAsync(
        Guid tenantId,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running reasoning execution.
    /// </summary>
    /// <param name="executionId">Execution ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cancelled, false if not found or already completed</returns>
    Task<bool> CancelExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);
}
