using SerialMemory.Core.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace SerialMemory.ML;

/// <summary>
/// Embedding service that calls Python sentence-transformers via subprocess
/// This is simpler and more reliable than ONNX for getting started
/// </summary>
public class PythonEmbeddingService(
    string pythonPath = "python",
    string modelName = "sentence-transformers/all-MiniLM-L6-v2")
    : IEmbeddingService
{
    private const int EmbeddingDim = 384; // all-MiniLM-L6-v2 produces 384-dim vectors

    public int EmbeddingDimension => EmbeddingDim;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var escapedText = text.Replace("\"", "\\\"").Replace("\n", " ");

        var pythonScript = $@"
import json
from sentence_transformers import SentenceTransformer
model = SentenceTransformer('{modelName}')
embedding = model.encode(""{escapedText}"", normalize_embeddings=True)
print(json.dumps(embedding.tolist()))
";

        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"-c \"{pythonScript.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Failed to start Python process");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new Exception($"Python embedding failed: {error}");

        var embedding = JsonSerializer.Deserialize<float[]>(output.Trim());
        return embedding ?? throw new Exception("Failed to parse embedding");
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        // For simplicity, embed one at a time
        // In production, you'd want to batch these properly
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            results.Add(await EmbedTextAsync(text, cancellationToken));
        }
        return results;
    }
}
