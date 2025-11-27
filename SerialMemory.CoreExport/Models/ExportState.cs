using System.Text.Json.Serialization;

namespace SerialMemory.CoreExport.Models;

public class ExportState
{
    [JsonPropertyName("lastWorkspace")]
    public string? LastWorkspace { get; set; }

    [JsonPropertyName("lastCursor")]
    public string? LastCursor { get; set; }

    [JsonPropertyName("completedWorkspaces")]
    public List<string> CompletedWorkspaces { get; set; } = new();

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
