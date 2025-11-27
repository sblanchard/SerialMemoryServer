using System.Text.Json.Serialization;

namespace SerialMemory.CoreExport.Models;

public class Workspace
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class WorkspaceResponse
{
    [JsonPropertyName("items")]
    public List<Workspace> Items { get; set; } = new();
}
