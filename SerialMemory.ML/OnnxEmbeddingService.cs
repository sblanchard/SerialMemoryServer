using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Configuration for ONNX embedding models.
/// Different models have different architectures and output handling requirements.
/// </summary>
public record OnnxModelConfig
{
    /// <summary>Model name for logging/identification</summary>
    public string ModelName { get; init; } = "unknown";

    /// <summary>Expected embedding dimension (0 = auto-detect from model)</summary>
    public int ExpectedDimension { get; init; } = 0;

    /// <summary>Maximum sequence length the model supports</summary>
    public int MaxSequenceLength { get; init; } = 512;

    /// <summary>Whether this model needs token_type_ids input</summary>
    public bool RequiresTokenTypeIds { get; init; } = true;

    /// <summary>Pooling strategy: 'mean', 'cls', 'max'</summary>
    public string PoolingStrategy { get; init; } = "mean";

    /// <summary>Whether to normalize output embeddings</summary>
    public bool NormalizeOutput { get; init; } = true;

    /// <summary>Whether vocab uses lowercase tokens</summary>
    public bool LowercaseVocab { get; init; } = true;

    /// <summary>Pre-configured settings for common models</summary>
    public static readonly Dictionary<string, OnnxModelConfig> KnownModels = new()
    {
        // Small models (384 dimensions)
        ["all-MiniLM-L6-v2"] = new OnnxModelConfig
        {
            ModelName = "all-MiniLM-L6-v2",
            ExpectedDimension = 384,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },
        ["all-MiniLM-L12-v2"] = new OnnxModelConfig
        {
            ModelName = "all-MiniLM-L12-v2",
            ExpectedDimension = 384,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },
        ["bge-small-en-v1.5"] = new OnnxModelConfig
        {
            ModelName = "bge-small-en-v1.5",
            ExpectedDimension = 384,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "cls",
            NormalizeOutput = true
        },
        ["e5-small-v2"] = new OnnxModelConfig
        {
            ModelName = "e5-small-v2",
            ExpectedDimension = 384,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },

        // Medium models (768 dimensions)
        ["all-mpnet-base-v2"] = new OnnxModelConfig
        {
            ModelName = "all-mpnet-base-v2",
            ExpectedDimension = 768,
            MaxSequenceLength = 384,
            RequiresTokenTypeIds = false,  // MPNet doesn't use token_type_ids
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },
        ["bge-base-en-v1.5"] = new OnnxModelConfig
        {
            ModelName = "bge-base-en-v1.5",
            ExpectedDimension = 768,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "cls",
            NormalizeOutput = true
        },
        ["e5-base-v2"] = new OnnxModelConfig
        {
            ModelName = "e5-base-v2",
            ExpectedDimension = 768,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },
        ["gte-base"] = new OnnxModelConfig
        {
            ModelName = "gte-base",
            ExpectedDimension = 768,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },

        // Large models (1024 dimensions)
        ["e5-large-v2"] = new OnnxModelConfig
        {
            ModelName = "e5-large-v2",
            ExpectedDimension = 1024,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },
        ["bge-large-en-v1.5"] = new OnnxModelConfig
        {
            ModelName = "bge-large-en-v1.5",
            ExpectedDimension = 1024,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "cls",
            NormalizeOutput = true
        },
        ["gte-large"] = new OnnxModelConfig
        {
            ModelName = "gte-large",
            ExpectedDimension = 1024,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        },

        // XL models (higher dimensions)
        ["e5-mistral-7b-instruct"] = new OnnxModelConfig
        {
            ModelName = "e5-mistral-7b-instruct",
            ExpectedDimension = 4096,
            MaxSequenceLength = 4096,
            RequiresTokenTypeIds = false,
            PoolingStrategy = "last",  // Last token pooling
            NormalizeOutput = true
        }
    };
}

/// <summary>
/// Pure C# ONNX-based embedding service for sentence-transformers models.
/// No Python dependencies required.
///
/// Supports various embedding models with automatic dimension detection:
/// - 384-dim: all-MiniLM-L6-v2, all-MiniLM-L12-v2, bge-small-en-v1.5, e5-small-v2
/// - 768-dim: all-mpnet-base-v2, bge-base-en-v1.5, e5-base-v2, gte-base
/// - 1024-dim: e5-large-v2, bge-large-en-v1.5, gte-large
/// - 4096-dim: e5-mistral-7b-instruct
///
/// Export model from sentence-transformers:
/// ```bash
/// pip install optimum[exporters] onnx
/// optimum-cli export onnx --model sentence-transformers/all-mpnet-base-v2 ./onnx_model/
/// ```
/// </summary>
public partial class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocab;
    private readonly int _embeddingDimension;
    private readonly OnnxModelConfig _config;
    private readonly ILogger? _logger;

    // Special token IDs (BERT)
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly int _padTokenId;
    private readonly int _unkTokenId;

    /// <summary>
    /// Create an ONNX embedding service with automatic model detection.
    /// </summary>
    public OnnxEmbeddingService(string modelPath, string vocabPath, OnnxModelConfig? config = null, ILogger? logger = null)
    {
        _logger = logger;

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found at: {modelPath}");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found at: {vocabPath}");

        // Try to auto-detect model from path
        _config = config ?? DetectModelConfig(modelPath);

        // Load ONNX model with optimizations
        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        // Enable parallel execution for larger models
        sessionOptions.InterOpNumThreads = Environment.ProcessorCount;
        sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;

        _session = new InferenceSession(modelPath, sessionOptions);

        // Load vocabulary
        _vocab = LoadVocabulary(vocabPath, _config.LowercaseVocab);

        // Get special token IDs
        _clsTokenId = _vocab.GetValueOrDefault("[CLS]", 101);
        _sepTokenId = _vocab.GetValueOrDefault("[SEP]", 102);
        _padTokenId = _vocab.GetValueOrDefault("[PAD]", 0);
        _unkTokenId = _vocab.GetValueOrDefault("[UNK]", 100);

        // Auto-detect embedding dimension from model output
        _embeddingDimension = DetectEmbeddingDimension();

        _logger?.LogInformation(
            "ONNX embedding service initialized: Model={ModelName}, Dimension={Dimension}, MaxSeq={MaxSeq}, Pooling={Pooling}",
            _config.ModelName, _embeddingDimension, _config.MaxSequenceLength, _config.PoolingStrategy);
    }

    /// <summary>
    /// Get information about the loaded model
    /// </summary>
    public OnnxModelInfo GetModelInfo() => new(
        _config.ModelName,
        _embeddingDimension,
        _config.MaxSequenceLength,
        _config.PoolingStrategy,
        _vocab.Count,
        _session.InputMetadata.Keys.ToList(),
        _session.OutputMetadata.Keys.ToList()
    );

    public int EmbeddingDimension => _embeddingDimension;

    public Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => EmbedText(text), cancellationToken);
    }

    public Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<float[]>();
            foreach (var text in texts)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                results.Add(EmbedText(text));
            }
            return results;
        }, cancellationToken);
    }

    private float[] EmbedText(string text)
    {
        // Tokenize using WordPiece
        var tokens = TokenizeWordPiece(text);

        // Add [CLS] at start and [SEP] at end
        var inputTokens = new List<int> { _clsTokenId };
        inputTokens.AddRange(tokens.Take(_config.MaxSequenceLength - 2)); // Leave room for CLS and SEP
        inputTokens.Add(_sepTokenId);

        var seqLen = inputTokens.Count;

        // Create input tensors
        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = inputTokens[i];
            attentionMask[0, i] = 1;
        }

        // Build inputs list
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        };

        // Add token_type_ids if required by model
        if (_config.RequiresTokenTypeIds && _session.InputMetadata.ContainsKey("token_type_ids"))
        {
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });
            for (int i = 0; i < seqLen; i++)
            {
                tokenTypeIds[0, i] = 0; // Single sentence
            }
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var results = _session.Run(inputs);

        // Get the output tensor - prefer sentence_embedding if available (pre-pooled)
        // Otherwise fall back to last_hidden_state or first output
        var outputNames = _session.OutputMetadata.Keys.ToList();
        string outputName;

        if (outputNames.Contains("sentence_embedding"))
        {
            outputName = "sentence_embedding";
        }
        else if (outputNames.Contains("last_hidden_state"))
        {
            outputName = "last_hidden_state";
        }
        else
        {
            outputName = outputNames.First();
        }

        var output = results.First(r => r.Name == outputName);
        var outputTensor = output.AsTensor<float>();

        // Check tensor dimensions to determine if pooling is needed
        // 2D output [batch, hidden] = already pooled, skip pooling
        // 3D output [batch, seq, hidden] = needs pooling
        float[] pooled;
        if (outputTensor.Dimensions.Length == 2)
        {
            // Already pooled (e.g., sentence_embedding output from optimum export)
            _logger?.LogDebug("Output is 2D (pre-pooled), skipping pooling strategy");
            pooled = new float[_embeddingDimension];
            for (int j = 0; j < _embeddingDimension; j++)
            {
                pooled[j] = outputTensor[0, j];
            }
        }
        else if (outputTensor.Dimensions.Length == 3)
        {
            // Apply pooling strategy to token-level embeddings
            pooled = _config.PoolingStrategy.ToLowerInvariant() switch
            {
                "cls" => ClsPooling(outputTensor),
                "max" => MaxPooling(outputTensor, seqLen),
                "last" => LastTokenPooling(outputTensor, seqLen),
                _ => MeanPooling(outputTensor, seqLen)  // Default to mean
            };
        }
        else
        {
            throw new InvalidOperationException(
                $"Unexpected output tensor dimensions: {outputTensor.Dimensions.Length}. Expected 2 (pre-pooled) or 3 (token-level).");
        }

        // Normalize if configured
        return _config.NormalizeOutput ? Normalize(pooled) : pooled;
    }

    private OnnxModelConfig DetectModelConfig(string modelPath)
    {
        var modelDir = Path.GetDirectoryName(modelPath) ?? "";
        var dirName = Path.GetFileName(modelDir)?.ToLowerInvariant() ?? "";

        // Try to match known models
        foreach (var (key, config) in OnnxModelConfig.KnownModels)
        {
            if (dirName.Contains(key.ToLowerInvariant().Replace("-", "")) ||
                dirName.Contains(key.ToLowerInvariant()))
            {
                _logger?.LogInformation("Auto-detected model configuration: {ModelName}", config.ModelName);
                return config;
            }
        }

        // Check for config.json in model directory
        var configPath = Path.Combine(modelDir, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var hiddenSize = root.TryGetProperty("hidden_size", out var hs) ? hs.GetInt32() : 384;
                var maxPosition = root.TryGetProperty("max_position_embeddings", out var mp) ? mp.GetInt32() : 512;

                _logger?.LogInformation("Detected model from config.json: Dimension={Dimension}", hiddenSize);

                return new OnnxModelConfig
                {
                    ModelName = dirName,
                    ExpectedDimension = hiddenSize,
                    MaxSequenceLength = Math.Min(maxPosition, 512),
                    RequiresTokenTypeIds = true,
                    PoolingStrategy = "mean",
                    NormalizeOutput = true
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse config.json, using defaults");
            }
        }

        _logger?.LogWarning("Unknown model, using default configuration (384-dim, mean pooling)");
        return new OnnxModelConfig
        {
            ModelName = dirName,
            ExpectedDimension = 384,
            MaxSequenceLength = 512,
            RequiresTokenTypeIds = true,
            PoolingStrategy = "mean",
            NormalizeOutput = true
        };
    }

    private int DetectEmbeddingDimension()
    {
        // Try to get dimension from model output metadata
        var outputMeta = _session.OutputMetadata.Values.FirstOrDefault();
        if (outputMeta?.Dimensions != null && outputMeta.Dimensions.Length >= 2)
        {
            var lastDim = outputMeta.Dimensions[^1];
            if (lastDim > 0)
            {
                _logger?.LogDebug("Detected embedding dimension from model output: {Dimension}", lastDim);
                return lastDim;
            }
        }

        // Fall back to config
        if (_config.ExpectedDimension > 0)
        {
            return _config.ExpectedDimension;
        }

        // Run a test inference to detect dimension
        try
        {
            var testInput = new DenseTensor<long>(new[] { 1, 3 });
            testInput[0, 0] = _clsTokenId;
            testInput[0, 1] = _unkTokenId;
            testInput[0, 2] = _sepTokenId;

            var testMask = new DenseTensor<long>(new[] { 1, 3 });
            testMask[0, 0] = 1;
            testMask[0, 1] = 1;
            testMask[0, 2] = 1;

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", testInput),
                NamedOnnxValue.CreateFromTensor("attention_mask", testMask)
            };

            if (_session.InputMetadata.ContainsKey("token_type_ids"))
            {
                var typeIds = new DenseTensor<long>(new[] { 1, 3 });
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", typeIds));
            }

            using var results = _session.Run(inputs);
            var output = results.First();
            var tensor = output.AsTensor<float>();
            var dimension = tensor.Dimensions[^1];

            _logger?.LogDebug("Detected embedding dimension from test inference: {Dimension}", dimension);
            return dimension;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to detect dimension via test inference, using default 384");
            return 384;
        }
    }

    private List<int> TokenizeWordPiece(string text)
    {
        var tokens = new List<int>();

        // Basic preprocessing
        if (_config.LowercaseVocab)
            text = text.ToLowerInvariant();
        text = text.Trim();

        // Split into words
        var words = WordSplitRegex().Split(text)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();

        foreach (var word in words)
        {
            var wordTokens = TokenizeWord(word);
            tokens.AddRange(wordTokens);
        }

        return tokens;
    }

    private List<int> TokenizeWord(string word)
    {
        var tokens = new List<int>();

        // Check if whole word is in vocab
        if (_vocab.TryGetValue(word, out var wordId))
        {
            tokens.Add(wordId);
            return tokens;
        }

        // WordPiece tokenization
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var foundToken = false;

            while (start < end)
            {
                var substr = word[start..end];
                if (start > 0)
                    substr = "##" + substr;

                if (_vocab.TryGetValue(substr, out var tokenId))
                {
                    tokens.Add(tokenId);
                    foundToken = true;
                    start = end;
                    break;
                }
                end--;
            }

            if (!foundToken)
            {
                // Unknown token
                tokens.Add(_unkTokenId);
                start++;
            }
        }

        return tokens;
    }

    private float[] MeanPooling(Tensor<float> tensor, int seqLen)
    {
        var result = new float[_embeddingDimension];

        // tensor shape is [batch_size, seq_len, hidden_size]
        for (int i = 0; i < seqLen; i++)
        {
            for (int j = 0; j < _embeddingDimension; j++)
            {
                result[j] += tensor[0, i, j];
            }
        }

        for (int j = 0; j < _embeddingDimension; j++)
        {
            result[j] /= seqLen;
        }

        return result;
    }

    private float[] ClsPooling(Tensor<float> tensor)
    {
        var result = new float[_embeddingDimension];

        // Use the [CLS] token embedding (first position)
        for (int j = 0; j < _embeddingDimension; j++)
        {
            result[j] = tensor[0, 0, j];
        }

        return result;
    }

    private float[] MaxPooling(Tensor<float> tensor, int seqLen)
    {
        var result = new float[_embeddingDimension];

        // Initialize with first token
        for (int j = 0; j < _embeddingDimension; j++)
        {
            result[j] = tensor[0, 0, j];
        }

        // Max over sequence
        for (int i = 1; i < seqLen; i++)
        {
            for (int j = 0; j < _embeddingDimension; j++)
            {
                result[j] = Math.Max(result[j], tensor[0, i, j]);
            }
        }

        return result;
    }

    private float[] LastTokenPooling(Tensor<float> tensor, int seqLen)
    {
        var result = new float[_embeddingDimension];

        // Use the last token embedding (before padding)
        var lastIdx = seqLen - 1;
        for (int j = 0; j < _embeddingDimension; j++)
        {
            result[j] = tensor[0, lastIdx, j];
        }

        return result;
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(x => x * x));
        if (magnitude == 0) return vector;

        return vector.Select(x => x / magnitude).ToArray();
    }

    private static Dictionary<string, int> LoadVocabulary(string path, bool lowercase)
    {
        var vocab = new Dictionary<string, int>();
        var lines = File.ReadAllLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            var token = lines[i].Trim();
            if (!string.IsNullOrEmpty(token))
            {
                vocab[token] = i;
            }
        }

        return vocab;
    }

    [GeneratedRegex(@"\s+|(?<=[^\s\w])|(?=[^\s\w])")]
    private static partial Regex WordSplitRegex();

    public void Dispose()
    {
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Information about the loaded ONNX model
/// </summary>
public record OnnxModelInfo(
    string ModelName,
    int EmbeddingDimension,
    int MaxSequenceLength,
    string PoolingStrategy,
    int VocabularySize,
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames
);

/// <summary>
/// Factory for creating embedding services with automatic model download
/// </summary>
public static class EmbeddingServiceFactory
{
    /// <summary>
    /// Create an embedding service. Prefers ONNX if available, falls back to HTTP.
    /// </summary>
    public static IEmbeddingService Create(
        string? onnxModelPath = null,
        string? vocabPath = null,
        string? httpServiceUrl = null,
        ILogger? logger = null)
    {
        // Try ONNX first
        if (!string.IsNullOrEmpty(onnxModelPath) &&
            !string.IsNullOrEmpty(vocabPath) &&
            File.Exists(onnxModelPath) &&
            File.Exists(vocabPath))
        {
            try
            {
                return new OnnxEmbeddingService(onnxModelPath, vocabPath, logger: logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to create ONNX embedding service, falling back to HTTP");
            }
        }

        // Fall back to HTTP service
        var url = httpServiceUrl ?? "http://localhost:8765";
        return new HttpEmbeddingService(url);
    }

    /// <summary>
    /// Create an ONNX embedding service with a specific model configuration.
    /// </summary>
    public static OnnxEmbeddingService CreateOnnx(
        string modelPath,
        string vocabPath,
        string modelName,
        ILogger? logger = null)
    {
        var config = OnnxModelConfig.KnownModels.TryGetValue(modelName, out var known)
            ? known
            : null;

        return new OnnxEmbeddingService(modelPath, vocabPath, config, logger);
    }
}
