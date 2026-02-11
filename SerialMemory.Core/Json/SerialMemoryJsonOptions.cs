using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.Core.Json;

/// <summary>
/// Shared, reusable <see cref="JsonSerializerOptions"/> presets.
/// Each instance is created once and cached (JsonSerializerOptions is thread-safe after first serialization).
/// </summary>
public static class SerialMemoryJsonOptions
{
    /// <summary>
    /// Default options: camelCase, no indentation, skip null values.
    /// Suitable for MCP protocol messages, API responses, and event store serialization.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Default options with string enum converter.
    /// Suitable for event sourcing, projections, and export.
    /// </summary>
    public static JsonSerializerOptions WithEnums { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Indented output for human-readable exports and logging.
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Case-insensitive deserialization for reading external data.
    /// </summary>
    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
