namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// Memory layers in the cognitive hierarchy.
/// Each memory belongs to exactly one layer.
/// Layer transitions are explicit events.
/// </summary>
public enum MemoryLayer
{
    /// <summary>Raw transcript or input data</summary>
    L0_RAW = 0,

    /// <summary>Contextual understanding of raw input</summary>
    L1_CONTEXT = 1,

    /// <summary>Summarized information</summary>
    L2_SUMMARY = 2,

    /// <summary>Extracted knowledge and facts</summary>
    L3_KNOWLEDGE = 3,

    /// <summary>Heuristics and learned patterns</summary>
    L4_HEURISTIC = 4
}
