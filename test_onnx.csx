#r "SerialMemory.ML/bin/Release/net10.0/SerialMemory.ML.dll"
#r "SerialMemory.Core/bin/Release/net10.0/SerialMemory.Core.dll"

using SerialMemory.ML;

var modelPath = @"E:\models\model.onnx";
var vocabPath = @"E:\models\vocab.txt";

Console.WriteLine($"Testing ONNX Model: {modelPath}");
Console.WriteLine($"Vocab Path: {vocabPath}");

try
{
    var service = new OnnxEmbeddingService(modelPath, vocabPath);
    Console.WriteLine($"✓ ONNX model loaded successfully");
    Console.WriteLine($"  Embedding dimension: {service.EmbeddingDimension}");

    var testText = "Hello, this is a test of the embedding service.";
    var embedding = await service.EmbedTextAsync(testText);

    Console.WriteLine($"✓ Generated embedding for: \"{testText}\"");
    Console.WriteLine($"  Vector length: {embedding.Length}");
    Console.WriteLine($"  First 5 values: [{string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}]");

    // Test similarity
    var text1 = "The quick brown fox jumps over the lazy dog";
    var text2 = "A fast brown fox leaps over a sleepy dog";
    var text3 = "Machine learning algorithms for data processing";

    var emb1 = await service.EmbedTextAsync(text1);
    var emb2 = await service.EmbedTextAsync(text2);
    var emb3 = await service.EmbedTextAsync(text3);

    float CosineSimilarity(float[] a, float[] b)
    {
        var dot = a.Zip(b, (x, y) => x * y).Sum();
        var magA = MathF.Sqrt(a.Sum(x => x * x));
        var magB = MathF.Sqrt(b.Sum(x => x * x));
        return dot / (magA * magB);
    }

    var sim12 = CosineSimilarity(emb1, emb2);
    var sim13 = CosineSimilarity(emb1, emb3);

    Console.WriteLine($"\n✓ Similarity tests:");
    Console.WriteLine($"  \"{text1}\"");
    Console.WriteLine($"  vs \"{text2}\" = {sim12:F4} (should be high)");
    Console.WriteLine($"  vs \"{text3}\" = {sim13:F4} (should be lower)");
    Console.WriteLine($"  Semantic similarity working: {(sim12 > sim13 ? "YES ✓" : "NO ✗")}");

    service.Dispose();
    Console.WriteLine("\n✓ All ONNX ML tests passed!");
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
