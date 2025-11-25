using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SerialMemory.Core.Interfaces;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Pure C# ONNX-based embedding service for sentence-transformers models.
/// No Python dependencies required.
///
/// Model files required:
/// - model.onnx: ONNX model exported from sentence-transformers
/// - vocab.txt: BERT vocabulary file
/// - tokenizer_config.json: Optional tokenizer configuration
///
/// Export model from sentence-transformers:
/// ```python
/// from sentence_transformers import SentenceTransformer
/// model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')
/// model.save('model_dir')  # Save model
/// # Then use optimum-cli to export to ONNX:
/// # optimum-cli export onnx --model sentence-transformers/all-MiniLM-L6-v2 onnx_model/
/// ```
/// </summary>
public partial class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocab;
    private readonly int _embeddingDimension;
    private const int MaxSequenceLength = 512;

    // Special token IDs (BERT)
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly int _padTokenId;
    private readonly int _unkTokenId;

    public OnnxEmbeddingService(string modelPath, string vocabPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found at: {modelPath}");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found at: {vocabPath}");

        // Load ONNX model
        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, sessionOptions);

        // Load vocabulary
        _vocab = LoadVocabulary(vocabPath);

        // Get special token IDs
        _clsTokenId = _vocab.GetValueOrDefault("[CLS]", 101);
        _sepTokenId = _vocab.GetValueOrDefault("[SEP]", 102);
        _padTokenId = _vocab.GetValueOrDefault("[PAD]", 0);
        _unkTokenId = _vocab.GetValueOrDefault("[UNK]", 100);

        // Get embedding dimension from model output (384 for all-MiniLM-L6-v2)
        _embeddingDimension = 384; // Default for all-MiniLM-L6-v2
    }

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
        inputTokens.AddRange(tokens.Take(MaxSequenceLength - 2)); // Leave room for CLS and SEP
        inputTokens.Add(_sepTokenId);

        var seqLen = inputTokens.Count;

        // Create input tensors
        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = inputTokens[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0; // Single sentence
        }

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
        };

        using var results = _session.Run(inputs);

        // Get the sentence embedding (mean pooling of last hidden state)
        var outputName = _session.OutputMetadata.Keys.First();
        var output = results.First(r => r.Name == outputName);
        var outputTensor = output.AsTensor<float>();

        // Mean pooling over sequence dimension
        var pooled = MeanPooling(outputTensor, seqLen);

        // Normalize
        return Normalize(pooled);
    }

    private List<int> TokenizeWordPiece(string text)
    {
        var tokens = new List<int>();

        // Basic preprocessing
        text = text.ToLowerInvariant().Trim();

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

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(x => x * x));
        if (magnitude == 0) return vector;

        return vector.Select(x => x / magnitude).ToArray();
    }

    private static Dictionary<string, int> LoadVocabulary(string path)
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
        string? httpServiceUrl = null)
    {
        // Try ONNX first
        if (!string.IsNullOrEmpty(onnxModelPath) &&
            !string.IsNullOrEmpty(vocabPath) &&
            File.Exists(onnxModelPath) &&
            File.Exists(vocabPath))
        {
            try
            {
                return new OnnxEmbeddingService(onnxModelPath, vocabPath);
            }
            catch
            {
                // Fall through to HTTP
            }
        }

        // Fall back to HTTP service
        var url = httpServiceUrl ?? "http://localhost:8765";
        return new HttpEmbeddingService(url);
    }
}
