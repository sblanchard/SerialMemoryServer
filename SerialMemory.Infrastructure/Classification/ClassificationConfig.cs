namespace SerialMemory.Infrastructure.Classification;

/// <summary>
/// Configuration for cognitive memory maturation.
/// Memories dwell at each layer for a configured duration before advancing,
/// giving contradiction detection time to find and resolve conflicts.
/// </summary>
public sealed class ClassificationConfig
{
    /// <summary>Dwell time after L0 before advancing to L1. Default: immediate.</summary>
    public TimeSpan DwellTimeL0 { get; set; } = TimeSpan.Zero;

    /// <summary>Dwell time after L1 before advancing to L2. Default: 1 day.</summary>
    public TimeSpan DwellTimeL1 { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Dwell time after L2 before advancing to L3. Default: 3 days.</summary>
    public TimeSpan DwellTimeL2 { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Dwell time after L3 before advancing to L4. Default: 7 days.</summary>
    public TimeSpan DwellTimeL3 { get; set; } = TimeSpan.FromDays(7);

    /// <summary>L4 is terminal - no dwell needed.</summary>
    public TimeSpan DwellTimeL4 { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cutoff date: memories created before this skip dwell times.
    /// Set to deployment time so existing memories aren't blocked.
    /// </summary>
    public DateTimeOffset DwellCutoffDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the required dwell time for a layer before the NEXT layer can be generated.
    /// </summary>
    public TimeSpan GetDwellTime(string layer) => layer switch
    {
        "L0_RAW" => DwellTimeL0,
        "L1_CONTEXT" => DwellTimeL1,
        "L2_SUMMARY" => DwellTimeL2,
        "L3_KNOWLEDGE" => DwellTimeL3,
        "L4_HEURISTIC" => DwellTimeL4,
        _ => TimeSpan.Zero
    };

    /// <summary>
    /// Gets the previous layer name for a given layer.
    /// </summary>
    public static string? GetPreviousLayer(string layer) => layer switch
    {
        "L1_CONTEXT" => "L0_RAW",
        "L2_SUMMARY" => "L1_CONTEXT",
        "L3_KNOWLEDGE" => "L2_SUMMARY",
        "L4_HEURISTIC" => "L3_KNOWLEDGE",
        _ => null
    };

    public static ClassificationConfig Default => new();
}
