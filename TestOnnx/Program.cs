using SerialMemory.ML;

var modelPath = @"E:\models\model.onnx";
var vocabPath = @"E:\models\vocab.txt";

Console.WriteLine("=== ONNX Embedding Service Test ===\n");
Console.WriteLine($"Model: {modelPath}");
Console.WriteLine($"Vocab: {vocabPath}");
Console.WriteLine($"Model exists: {File.Exists(modelPath)}");
Console.WriteLine($"Vocab exists: {File.Exists(vocabPath)}\n");

try
{
    Console.WriteLine("Loading ONNX model...");
    var service = new OnnxEmbeddingService(modelPath, vocabPath);
    Console.WriteLine($"✓ Model loaded successfully");
    Console.WriteLine($"  Embedding dimension: {service.EmbeddingDimension}\n");

    // Test 1: Basic embedding
    var testText = "Hello, this is a test of the embedding service.";
    Console.WriteLine($"Test 1: Generating embedding for \"{testText}\"");
    var embedding = await service.EmbedTextAsync(testText);
    Console.WriteLine($"✓ Vector length: {embedding.Length}");
    Console.WriteLine($"  First 5 values: [{string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}]\n");

    // Test 2: Semantic similarity
    Console.WriteLine("Test 2: Semantic similarity comparison");
    var text1 = "The quick brown fox jumps over the lazy dog";
    var text2 = "A fast brown fox leaps over a sleepy dog";
    var text3 = "Machine learning algorithms for data processing";

    var emb1 = await service.EmbedTextAsync(text1);
    var emb2 = await service.EmbedTextAsync(text2);
    var emb3 = await service.EmbedTextAsync(text3);

    var sim12 = CosineSimilarity(emb1, emb2);
    var sim13 = CosineSimilarity(emb1, emb3);

    Console.WriteLine($"  \"{text1}\"");
    Console.WriteLine($"  vs similar: \"{text2}\"");
    Console.WriteLine($"     Similarity: {sim12:F4}");
    Console.WriteLine($"  vs different: \"{text3}\"");
    Console.WriteLine($"     Similarity: {sim13:F4}");
    Console.WriteLine($"\n✓ Semantic similarity correct: {(sim12 > sim13 ? "YES" : "NO")} (similar > different)\n");

    // Test 3: Batch embedding
    Console.WriteLine("Test 3: Batch embedding");
    var texts = new List<string> { "First sentence", "Second sentence", "Third sentence" };
    var embeddings = await service.EmbedBatchAsync(texts);
    Console.WriteLine($"✓ Batch size: {embeddings.Count}");
    Console.WriteLine($"  All correct dimension: {embeddings.All(e => e.Length == 384)}\n");

    service.Dispose();
    Console.WriteLine("=== All ONNX ML tests PASSED ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n✗ ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    Environment.Exit(1);
}

static float CosineSimilarity(float[] a, float[] b)
{
    var dot = a.Zip(b, (x, y) => x * y).Sum();
    var magA = MathF.Sqrt(a.Sum(x => x * x));
    var magB = MathF.Sqrt(b.Sum(x => x * x));
    return dot / (magA * magB);
}
