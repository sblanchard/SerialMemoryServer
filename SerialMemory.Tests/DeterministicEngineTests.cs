using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SerialMemory.Core.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace SerialMemory.Tests;

/// <summary>
/// Deterministic execution tests for the ONNX-based cognitive memory engine.
/// </summary>
public class DeterministicEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void SeededRandom_ProducesDeterministicSequence()
    {
        // Arrange
        const long seed = 42;
        var sequence1 = new List<int>();
        var sequence2 = new List<int>();

        // Act
        var random1 = new Random((int)seed);
        var random2 = new Random((int)seed);

        for (int i = 0; i < 100; i++)
        {
            sequence1.Add(random1.Next());
            sequence2.Add(random2.Next());
        }

        // Assert
        Assert.Equal(sequence1, sequence2);
        output.WriteLine($"Generated {sequence1.Count} deterministic random numbers with seed {seed}");
    }

    [Fact]
    public void ContentHash_IsDeterministic()
    {
        // Arrange
        var content = "This is a test memory content for hashing.";

        // Act
        var hash1 = ComputeHash(content);
        var hash2 = ComputeHash(content);

        // Different content should produce different hash
        var differentHash = ComputeHash(content + " modified");

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, differentHash);
        Assert.Equal(64, hash1.Length); // SHA256 produces 64 hex chars

        output.WriteLine($"Content: '{content}'");
        output.WriteLine($"Hash: {hash1}");
    }

    [Fact]
    public void EmbeddingNormalization_IsDeterministic()
    {
        // Arrange
        var embedding = new float[] { 0.5f, 0.3f, 0.8f, 0.1f, 0.9f };

        // Act
        var normalized1 = NormalizeEmbedding(embedding);
        var normalized2 = NormalizeEmbedding(embedding);

        // Assert
        Assert.Equal(normalized1.Length, normalized2.Length);
        for (int i = 0; i < normalized1.Length; i++)
        {
            Assert.Equal(normalized1[i], normalized2[i], 6);
        }

        // Verify L2 norm is 1
        var norm = MathF.Sqrt(normalized1.Sum(x => x * x));
        Assert.Equal(1.0f, norm, 5);

        output.WriteLine($"Original: [{string.Join(", ", embedding.Select(x => x.ToString("F4")))}]");
        output.WriteLine($"Normalized: [{string.Join(", ", normalized1.Select(x => x.ToString("F4")))}]");
    }

    [Fact]
    public void CosineSimilarity_IsDeterministic()
    {
        // Arrange
        var embedding1 = new float[] { 0.5f, 0.3f, 0.8f, 0.1f };
        var embedding2 = new float[] { 0.4f, 0.5f, 0.7f, 0.2f };

        // Act
        var similarity1 = CosineSimilarity(embedding1, embedding2);
        var similarity2 = CosineSimilarity(embedding1, embedding2);

        // Assert
        Assert.Equal(similarity1, similarity2, 6);
        Assert.InRange(similarity1, -1.0f, 1.0f);

        output.WriteLine($"Similarity: {similarity1:F6}");
    }

    [Fact]
    public void KMeansClustering_IsDeterministicWithSeed()
    {
        // Arrange
        var embeddings = GenerateDeterministicEmbeddings(100, 384, seed: 12345);
        const int k = 5;

        // Act
        var (centroids1, assignments1) = KMeansClustering(embeddings, k, seed: 42);
        var (centroids2, assignments2) = KMeansClustering(embeddings, k, seed: 42);

        // Assert
        Assert.Equal(assignments1.Length, assignments2.Length);
        for (int i = 0; i < assignments1.Length; i++)
        {
            Assert.Equal(assignments1[i], assignments2[i]);
        }

        output.WriteLine($"Clustered {embeddings.Length} embeddings into {k} clusters deterministically");
        output.WriteLine($"Cluster sizes: {string.Join(", ", Enumerable.Range(0, k).Select(c => assignments1.Count(a => a == c)))}");
    }

    [Fact]
    public void PriorityScoring_IsDeterministic()
    {
        // Arrange
        var candidates = new[]
        {
            new MemoryCandidate(Guid.NewGuid(), "Content 1", 50, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, DateTime.UtcNow),
            new MemoryCandidate(Guid.NewGuid(), "Content 2", 100, 0.8f, 0.9f, 0.6f, 0.7f, 0.4f, DateTime.UtcNow),
            new MemoryCandidate(Guid.NewGuid(), "Content 3", 75, 0.7f, 0.7f, 0.9f, 0.8f, 0.6f, DateTime.UtcNow),
        };

        var weights = new PriorityWeights(0.2f, 0.3f, 0.2f, 0.15f, 0.15f);

        // Act
        var scores1 = candidates.Select(c => ComputePriorityScore(c, weights)).ToList();
        var scores2 = candidates.Select(c => ComputePriorityScore(c, weights)).ToList();

        // Assert
        Assert.Equal(scores1, scores2);

        for (int i = 0; i < candidates.Length; i++)
        {
            output.WriteLine($"Candidate {i + 1}: Score = {scores1[i]:F4}");
        }
    }

    [Fact]
    public void ExponentialDecay_IsDeterministic()
    {
        // Arrange
        var originalConfidence = 1.0f;
        var halfLifeDays = 30.0f;
        var testDays = new[] { 0f, 15f, 30f, 45f, 60f, 90f };

        // Act
        var decayedValues1 = testDays.Select(d => ApplyExponentialDecay(originalConfidence, halfLifeDays, d)).ToList();
        var decayedValues2 = testDays.Select(d => ApplyExponentialDecay(originalConfidence, halfLifeDays, d)).ToList();

        // Assert
        Assert.Equal(decayedValues1, decayedValues2);

        // Verify decay properties
        Assert.Equal(1.0f, decayedValues1[0], 5); // Day 0: no decay
        Assert.Equal(0.5f, decayedValues1[2], 2); // Day 30: half-life
        Assert.Equal(0.25f, decayedValues1[4], 2); // Day 60: quarter

        output.WriteLine("Exponential Decay (half-life = 30 days):");
        for (int i = 0; i < testDays.Length; i++)
        {
            output.WriteLine($"  Day {testDays[i]:F0}: {decayedValues1[i]:F4}");
        }
    }

    [Fact]
    public void ReasoningChainHash_IsDeterministic()
    {
        // Arrange
        var steps = new List<(string Type, string Content)>
        {
            ("query_understanding", "Understanding: What is X?"),
            ("evidence", "Found 3 relevant memories"),
            ("synthesis", "Synthesized key facts"),
            ("conclusion", "Final answer based on evidence")
        };

        // Act
        var hash1 = ComputeReasoningChainHash(steps);
        var hash2 = ComputeReasoningChainHash(steps);

        // Assert
        Assert.Equal(hash1, hash2);

        output.WriteLine($"Reasoning chain hash: {hash1}");
    }

    [Fact]
    public void TokenEstimation_IsDeterministic()
    {
        // Arrange
        var texts = new[]
        {
            "Hello, world!",
            "This is a longer text that should produce more tokens.",
            "The quick brown fox jumps over the lazy dog. " +
            "Pack my box with five dozen liquor jugs. " +
            "How vexingly quick daft zebras jump!"
        };

        // Act
        var estimates1 = texts.Select(EstimateTokens).ToList();
        var estimates2 = texts.Select(EstimateTokens).ToList();

        // Assert
        Assert.Equal(estimates1, estimates2);

        for (int i = 0; i < texts.Length; i++)
        {
            output.WriteLine($"Text ({texts[i].Length} chars): ~{estimates1[i]} tokens");
        }
    }

    [Fact]
    public void ContextPacking_IsDeterministic()
    {
        // Arrange
        var candidates = Enumerable.Range(0, 20)
            .Select(i => new MemoryCandidate(
                Guid.NewGuid(),
                $"Memory content {i} with some additional text to vary token counts",
                20 + i * 5, // Varying token counts
                (float)(0.5 + i * 0.02), // Varying relevance
                0.8f,
                0.7f - i * 0.01f,
                0.5f,
                0.3f,
                DateTime.UtcNow.AddDays(-i)))
            .ToList();

        var weights = new PriorityWeights(0.2f, 0.4f, 0.2f, 0.1f, 0.1f);
        const int budget = 500;

        // Act
        var packed1 = PackContext(candidates, budget, weights);
        var packed2 = PackContext(candidates, budget, weights);

        // Assert
        Assert.Equal(packed1.Count, packed2.Count);
        for (int i = 0; i < packed1.Count; i++)
        {
            Assert.Equal(packed1[i].MemoryId, packed2[i].MemoryId);
            Assert.Equal(packed1[i].PriorityScore, packed2[i].PriorityScore, 5);
        }

        var totalTokens = packed1.Sum(p => p.TokenCount);
        output.WriteLine($"Packed {packed1.Count}/{candidates.Count} memories using {totalTokens}/{budget} tokens");
    }

    [Fact]
    public void ContradictionDetection_IsDeterministic()
    {
        // Arrange
        var facts = new[]
        {
            "John is a software engineer",
            "John is not a software engineer",
            "The project was completed in 2023",
            "The project was completed in 2024",
            "The system uses PostgreSQL database",
            "The system uses PostgreSQL database" // Duplicate, not contradiction
        };

        // Act
        var contradictions1 = DetectContradictions(facts);
        var contradictions2 = DetectContradictions(facts);

        // Assert
        Assert.Equal(contradictions1.Count, contradictions2.Count);
        for (int i = 0; i < contradictions1.Count; i++)
        {
            Assert.Equal(contradictions1[i].IndexA, contradictions2[i].IndexA);
            Assert.Equal(contradictions1[i].IndexB, contradictions2[i].IndexB);
        }

        output.WriteLine($"Found {contradictions1.Count} contradictions:");
        foreach (var (idxA, idxB, type) in contradictions1)
        {
            output.WriteLine($"  [{idxA}] vs [{idxB}]: {type}");
            output.WriteLine($"    A: {facts[idxA]}");
            output.WriteLine($"    B: {facts[idxB]}");
        }
    }

    // Helper methods that mirror the actual implementation

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static float[] NormalizeEmbedding(float[] embedding)
    {
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm == 0) return embedding;
        return embedding.Select(x => x / norm).ToArray();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        var normA = 0f;
        var normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private static float[][] GenerateDeterministicEmbeddings(int count, int dimension, int seed)
    {
        var random = new Random(seed);
        var embeddings = new float[count][];

        for (int i = 0; i < count; i++)
        {
            embeddings[i] = new float[dimension];
            for (int j = 0; j < dimension; j++)
            {
                embeddings[i][j] = (float)(random.NextDouble() * 2 - 1);
            }
            embeddings[i] = NormalizeEmbedding(embeddings[i]);
        }

        return embeddings;
    }

    private static (float[][] Centroids, int[] Assignments) KMeansClustering(float[][] embeddings, int k, int seed)
    {
        var random = new Random(seed);
        var n = embeddings.Length;
        var dimension = embeddings[0].Length;

        // K-means++ initialization
        var centroids = new float[k][];
        centroids[0] = (float[])embeddings[random.Next(n)].Clone();

        for (int c = 1; c < k; c++)
        {
            var distances = new float[n];
            var sumDistances = 0f;

            for (int i = 0; i < n; i++)
            {
                var minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    var dist = 1 - CosineSimilarity(embeddings[i], centroids[j]);
                    minDist = Math.Min(minDist, dist);
                }
                distances[i] = minDist * minDist;
                sumDistances += distances[i];
            }

            var threshold = random.NextSingle() * sumDistances;
            var cumSum = 0f;
            for (int i = 0; i < n; i++)
            {
                cumSum += distances[i];
                if (cumSum >= threshold)
                {
                    centroids[c] = (float[])embeddings[i].Clone();
                    break;
                }
            }
            centroids[c] ??= (float[])embeddings[random.Next(n)].Clone();
        }

        // K-means iterations
        var assignments = new int[n];
        for (int iter = 0; iter < 20; iter++)
        {
            var changed = false;

            // Assignment step
            for (int i = 0; i < n; i++)
            {
                var best = 0;
                var bestSim = CosineSimilarity(embeddings[i], centroids[0]);

                for (int c = 1; c < k; c++)
                {
                    var sim = CosineSimilarity(embeddings[i], centroids[c]);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        best = c;
                    }
                }

                if (assignments[i] != best)
                {
                    assignments[i] = best;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update step
            var newCentroids = new float[k][];
            var counts = new int[k];

            for (int c = 0; c < k; c++)
            {
                newCentroids[c] = new float[dimension];
            }

            for (int i = 0; i < n; i++)
            {
                var cluster = assignments[i];
                counts[cluster]++;
                for (int d = 0; d < dimension; d++)
                {
                    newCentroids[cluster][d] += embeddings[i][d];
                }
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    for (int d = 0; d < dimension; d++)
                    {
                        newCentroids[c][d] /= counts[c];
                    }
                    centroids[c] = NormalizeEmbedding(newCentroids[c]);
                }
            }
        }

        return (centroids, assignments);
    }

    private static float ComputePriorityScore(MemoryCandidate candidate, PriorityWeights weights)
    {
        return candidate.RecencyScore * weights.RecencyWeight
             + candidate.RelevanceScore * weights.RelevanceWeight
             + candidate.ConfidenceScore * weights.ConfidenceWeight
             + candidate.AffinityScore * weights.AffinityWeight
             + candidate.DirectiveScore * weights.DirectiveWeight;
    }

    private static float ApplyExponentialDecay(float originalConfidence, float halfLifeDays, float daysElapsed)
    {
        return originalConfidence * MathF.Pow(0.5f, daysElapsed / halfLifeDays);
    }

    private static string ComputeReasoningChainHash(List<(string Type, string Content)> steps)
    {
        var combined = string.Join("|", steps.Select(s => $"{s.Type}:{s.Content}"));
        return ComputeHash(combined);
    }

    private static int EstimateTokens(string text)
    {
        const float tokensPerChar = 0.3f;
        const int overhead = 20;
        return (int)(text.Length * tokensPerChar) + overhead;
    }

    private static List<PackedMemory> PackContext(
        List<MemoryCandidate> candidates,
        int budget,
        PriorityWeights weights)
    {
        var scored = candidates
            .Select(c => (Candidate: c, Score: ComputePriorityScore(c, weights)))
            .OrderByDescending(x => x.Score)
            .ToList();

        var packed = new List<PackedMemory>();
        var remaining = budget;
        var order = 0;

        foreach (var (candidate, score) in scored)
        {
            if (candidate.TokenCount > remaining) continue;

            packed.Add(new PackedMemory(
                candidate.MemoryId,
                candidate.Content,
                candidate.TokenCount,
                score,
                ++order));

            remaining -= candidate.TokenCount;
        }

        return packed;
    }

    private static List<(int IndexA, int IndexB, string Type)> DetectContradictions(string[] facts)
    {
        var contradictions = new List<(int, int, string)>();

        for (int i = 0; i < facts.Length; i++)
        {
            for (int j = i + 1; j < facts.Length; j++)
            {
                var type = CheckContradiction(facts[i], facts[j]);
                if (type != null)
                {
                    contradictions.Add((i, j, type));
                }
            }
        }

        return contradictions;
    }

    private static string? CheckContradiction(string a, string b)
    {
        var lowerA = a.ToLowerInvariant();
        var lowerB = b.ToLowerInvariant();

        // Check for negation
        var negationPatterns = new[] { "is not", "was not", "are not", "were not", "not a", "not the" };
        foreach (var pattern in negationPatterns)
        {
            if ((lowerA.Contains(pattern) && !lowerB.Contains(pattern) ||
                 !lowerA.Contains(pattern) && lowerB.Contains(pattern)) &&
                ComputeSimilarWithoutNegation(lowerA, lowerB) > 0.8)
            {
                return "negation";
            }
        }

        // Check for different years in similar context
        var yearsA = System.Text.RegularExpressions.Regex.Matches(lowerA, @"\b(19|20)\d{2}\b")
            .Select(m => m.Value).ToHashSet();
        var yearsB = System.Text.RegularExpressions.Regex.Matches(lowerB, @"\b(19|20)\d{2}\b")
            .Select(m => m.Value).ToHashSet();

        if (yearsA.Count > 0 && yearsB.Count > 0 && !yearsA.Overlaps(yearsB))
        {
            var withoutYearsA = System.Text.RegularExpressions.Regex.Replace(lowerA, @"\b(19|20)\d{2}\b", "YEAR");
            var withoutYearsB = System.Text.RegularExpressions.Regex.Replace(lowerB, @"\b(19|20)\d{2}\b", "YEAR");
            if (withoutYearsA == withoutYearsB)
            {
                return "temporal";
            }
        }

        return null;
    }

    private static float ComputeSimilarWithoutNegation(string a, string b)
    {
        var negations = new[] { "not ", "n't " };
        foreach (var neg in negations)
        {
            a = a.Replace(neg, "");
            b = b.Replace(neg, "");
        }

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0;
    }
}

/// <summary>
/// Benchmark tests for the deterministic engine.
/// </summary>
public class DeterministicEngineBenchmarks
{
    private readonly ITestOutputHelper _output;

    public DeterministicEngineBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark_EmbeddingGeneration()
    {
        // Arrange
        const int iterations = 1000;
        const int dimension = 384;
        var random = new Random(42);
        var texts = Enumerable.Range(0, iterations)
            .Select(_ => GenerateRandomText(random, 100))
            .ToList();

        // Act
        var sw = Stopwatch.StartNew();
        foreach (var text in texts)
        {
            // Simulate embedding generation with deterministic hash-based pseudo-embedding
            var embedding = GeneratePseudoEmbedding(text, dimension);
        }
        sw.Stop();

        // Report
        _output.WriteLine($"Embedding Generation Benchmark:");
        _output.WriteLine($"  Iterations: {iterations}");
        _output.WriteLine($"  Total time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Per embedding: {sw.ElapsedMilliseconds / (double)iterations:F2}ms");
        _output.WriteLine($"  Throughput: {iterations * 1000.0 / sw.ElapsedMilliseconds:F0} embeddings/sec");
    }

    [Fact]
    public void Benchmark_SemanticSearch()
    {
        // Arrange
        const int corpusSize = 10000;
        const int queryCount = 100;
        const int dimension = 384;

        var corpus = GenerateDeterministicEmbeddings(corpusSize, dimension, seed: 12345);
        var queries = GenerateDeterministicEmbeddings(queryCount, dimension, seed: 67890);

        // Act
        var sw = Stopwatch.StartNew();
        foreach (var query in queries)
        {
            var results = corpus
                .Select((emb, idx) => (Index: idx, Similarity: CosineSimilarity(query, emb)))
                .OrderByDescending(x => x.Similarity)
                .Take(10)
                .ToList();
        }
        sw.Stop();

        // Report
        _output.WriteLine($"Semantic Search Benchmark:");
        _output.WriteLine($"  Corpus size: {corpusSize}");
        _output.WriteLine($"  Query count: {queryCount}");
        _output.WriteLine($"  Dimension: {dimension}");
        _output.WriteLine($"  Total time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Per query: {sw.ElapsedMilliseconds / (double)queryCount:F2}ms");
        _output.WriteLine($"  Throughput: {queryCount * 1000.0 / sw.ElapsedMilliseconds:F0} queries/sec");
    }

    [Fact]
    public void Benchmark_KMeansClustering()
    {
        // Arrange
        var testCases = new[]
        {
            (DataSize: 1000, Clusters: 10, Dimension: 384),
            (DataSize: 5000, Clusters: 50, Dimension: 384),
            (DataSize: 10000, Clusters: 100, Dimension: 384),
        };

        _output.WriteLine("K-Means Clustering Benchmark:");

        foreach (var (dataSize, clusters, dimension) in testCases)
        {
            var embeddings = GenerateDeterministicEmbeddings(dataSize, dimension, seed: 42);

            var sw = Stopwatch.StartNew();
            var (centroids, assignments) = KMeansClustering(embeddings, clusters, seed: 42);
            sw.Stop();

            _output.WriteLine($"  {dataSize} points, {clusters} clusters: {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public void Benchmark_ContextPacking()
    {
        // Arrange
        var testCases = new[]
        {
            (Candidates: 50, Budget: 1000),
            (Candidates: 100, Budget: 2000),
            (Candidates: 500, Budget: 4000),
            (Candidates: 1000, Budget: 8000),
        };

        var weights = new PriorityWeights(0.2f, 0.3f, 0.2f, 0.15f, 0.15f);

        _output.WriteLine("Context Packing Benchmark:");

        foreach (var (candidateCount, budget) in testCases)
        {
            var random = new Random(42);
            var candidates = Enumerable.Range(0, candidateCount)
                .Select(i => new MemoryCandidate(
                    Guid.NewGuid(),
                    GenerateRandomText(random, 100 + random.Next(200)),
                    20 + random.Next(100),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    DateTime.UtcNow.AddDays(-random.Next(365))))
                .ToList();

            var sw = Stopwatch.StartNew();
            var packed = PackContext(candidates, budget, weights);
            sw.Stop();

            _output.WriteLine($"  {candidateCount} candidates, {budget} token budget: {sw.ElapsedMilliseconds}ms ({packed.Count} packed)");
        }
    }

    [Fact]
    public void Benchmark_HashComputation()
    {
        // Arrange
        const int iterations = 100000;
        var random = new Random(42);
        var texts = Enumerable.Range(0, iterations)
            .Select(_ => GenerateRandomText(random, 100))
            .ToList();

        // Act
        var sw = Stopwatch.StartNew();
        foreach (var text in texts)
        {
            var hash = ComputeHash(text);
        }
        sw.Stop();

        // Report
        _output.WriteLine($"Hash Computation Benchmark:");
        _output.WriteLine($"  Iterations: {iterations}");
        _output.WriteLine($"  Total time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Per hash: {sw.Elapsed.TotalMicroseconds / iterations:F2}μs");
        _output.WriteLine($"  Throughput: {iterations * 1000.0 / sw.ElapsedMilliseconds:F0} hashes/sec");
    }

    // Helper methods

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static float[] GeneratePseudoEmbedding(string text, int dimension)
    {
        // Generate deterministic pseudo-embedding from text
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var random = new Random(BitConverter.ToInt32(hash, 0));

        var embedding = new float[dimension];
        for (int i = 0; i < dimension; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2 - 1);
        }

        // Normalize
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        for (int i = 0; i < dimension; i++)
        {
            embedding[i] /= norm;
        }

        return embedding;
    }

    private static float[][] GenerateDeterministicEmbeddings(int count, int dimension, int seed)
    {
        var random = new Random(seed);
        var embeddings = new float[count][];

        for (int i = 0; i < count; i++)
        {
            embeddings[i] = new float[dimension];
            for (int j = 0; j < dimension; j++)
            {
                embeddings[i][j] = (float)(random.NextDouble() * 2 - 1);
            }

            // Normalize
            var norm = MathF.Sqrt(embeddings[i].Sum(x => x * x));
            for (int j = 0; j < dimension; j++)
            {
                embeddings[i][j] /= norm;
            }
        }

        return embeddings;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot; // Already normalized, so no need to divide by norms
    }

    private static (float[][] Centroids, int[] Assignments) KMeansClustering(float[][] embeddings, int k, int seed)
    {
        var random = new Random(seed);
        var n = embeddings.Length;
        var dimension = embeddings[0].Length;

        var centroids = new float[k][];
        for (int c = 0; c < k; c++)
        {
            centroids[c] = (float[])embeddings[random.Next(n)].Clone();
        }

        var assignments = new int[n];
        for (int iter = 0; iter < 20; iter++)
        {
            for (int i = 0; i < n; i++)
            {
                var best = 0;
                var bestSim = CosineSimilarity(embeddings[i], centroids[0]);
                for (int c = 1; c < k; c++)
                {
                    var sim = CosineSimilarity(embeddings[i], centroids[c]);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        best = c;
                    }
                }
                assignments[i] = best;
            }

            var newCentroids = new float[k][];
            var counts = new int[k];
            for (int c = 0; c < k; c++) newCentroids[c] = new float[dimension];

            for (int i = 0; i < n; i++)
            {
                counts[assignments[i]]++;
                for (int d = 0; d < dimension; d++)
                    newCentroids[assignments[i]][d] += embeddings[i][d];
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    var norm = 0f;
                    for (int d = 0; d < dimension; d++)
                    {
                        newCentroids[c][d] /= counts[c];
                        norm += newCentroids[c][d] * newCentroids[c][d];
                    }
                    norm = MathF.Sqrt(norm);
                    for (int d = 0; d < dimension; d++)
                        centroids[c][d] = newCentroids[c][d] / norm;
                }
            }
        }

        return (centroids, assignments);
    }

    private static string GenerateRandomText(Random random, int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz ";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(chars[random.Next(chars.Length)]);
        }
        return sb.ToString();
    }

    private static List<PackedMemory> PackContext(
        List<MemoryCandidate> candidates,
        int budget,
        PriorityWeights weights)
    {
        var scored = candidates
            .Select(c => (Candidate: c, Score: ComputeScore(c, weights)))
            .OrderByDescending(x => x.Score)
            .ToList();

        var packed = new List<PackedMemory>();
        var remaining = budget;
        var order = 0;

        foreach (var (candidate, score) in scored)
        {
            if (candidate.TokenCount > remaining) continue;
            packed.Add(new PackedMemory(candidate.MemoryId, candidate.Content, candidate.TokenCount, score, ++order));
            remaining -= candidate.TokenCount;
        }

        return packed;
    }

    private static float ComputeScore(MemoryCandidate c, PriorityWeights w) =>
        c.RecencyScore * w.RecencyWeight + c.RelevanceScore * w.RelevanceWeight +
        c.ConfidenceScore * w.ConfidenceWeight + c.AffinityScore * w.AffinityWeight +
        c.DirectiveScore * w.DirectiveWeight;
}
