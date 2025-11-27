using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Security pipeline for intercepting memory operations
/// </summary>
public interface ISecurityPipeline
{
    /// <summary>
    /// Validate memory on read - checks hash integrity
    /// </summary>
    Task<HashValidationResult> ValidateOnReadAsync(Memory memory, CancellationToken ct = default);

    /// <summary>
    /// Validate memory on write - generates hash and logs event
    /// </summary>
    Task<HashValidationResult> ValidateOnWriteAsync(Memory memory, CancellationToken ct = default);

    /// <summary>
    /// Detect contradictions for a memory against existing memories
    /// </summary>
    Task<List<ContradictionResult>> DetectContradictionsAsync(Memory memory, IEnumerable<Memory> candidates, CancellationToken ct = default);

    /// <summary>
    /// Detect causal loops starting from a memory
    /// </summary>
    Task<LoopDetectionResult> DetectLoopsAsync(Guid memoryId, Guid[] causalParents, CancellationToken ct = default);

    /// <summary>
    /// Run a full integrity scan on all memories
    /// </summary>
    Task<SecurityScan> RunFullIntegrityScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Run hash validation scan on a batch of memories
    /// </summary>
    Task<SecurityScan> RunHashValidationScanAsync(int batchSize = 100, CancellationToken ct = default);

    /// <summary>
    /// Run contradiction detection scan
    /// </summary>
    Task<SecurityScan> RunContradictionScanAsync(int batchSize = 50, float threshold = 0.85f, CancellationToken ct = default);

    /// <summary>
    /// Run loop detection scan
    /// </summary>
    Task<SecurityScan> RunLoopDetectionScanAsync(int maxDepth = 10, CancellationToken ct = default);
}
