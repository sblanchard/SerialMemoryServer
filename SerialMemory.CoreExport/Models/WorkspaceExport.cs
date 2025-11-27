using System.Text.Json.Serialization;

namespace SerialMemory.CoreExport.Models;

public class WorkspaceExport
{
    [JsonPropertyName("workspaceId")]
    public string WorkspaceId { get; set; } = string.Empty;

    [JsonPropertyName("workspaceName")]
    public string WorkspaceName { get; set; } = string.Empty;

    [JsonPropertyName("exportedAtUtc")]
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("memoryCount")]
    public int MemoryCount { get; set; }

    [JsonPropertyName("memories")]
    public List<MemoryItem> Memories { get; set; } = new();
}
