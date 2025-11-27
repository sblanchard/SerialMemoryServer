using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Interface for a reasoning model that can analyze input and produce insights.
/// </summary>
public interface IReasoningModel
{
    /// <summary>
    /// Unique name of the model
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Version identifier
    /// </summary>
    string Version { get; }

    /// <summary>
    /// The reasoning role this model fulfills
    /// </summary>
    ReasoningRole Role { get; }

    /// <summary>
    /// Maximum time to wait for this model's response
    /// </summary>
    TimeSpan Timeout { get; }

    /// <summary>
    /// Run reasoning on the input and produce results
    /// </summary>
    /// <param name="input">Reasoning input with context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Model result with insights</returns>
    Task<ModelResult> ReasonAsync(ReasoningInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating reasoning models
/// </summary>
public interface IReasoningModelFactory
{
    /// <summary>
    /// Create all configured reasoning models
    /// </summary>
    IEnumerable<IReasoningModel> CreateModels();

    /// <summary>
    /// Create models for a specific role
    /// </summary>
    IEnumerable<IReasoningModel> CreateModelsForRole(ReasoningRole role);
}
