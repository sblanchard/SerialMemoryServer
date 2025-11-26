namespace SerialMemory.CoreExport.Models;

public class CoreConfig
{
    public string BaseUrl { get; set; } = "https://core.heysol.ai/api";
    public string ApiKey { get; set; } = string.Empty;
    public string ExportPath { get; set; } = "./exports";
    public int PageSize { get; set; } = 100;
    public bool IncludeEmbeddings { get; set; } = false;
    public bool CompressOutput { get; set; } = false;
    public bool EncryptExports { get; set; } = false;
}
