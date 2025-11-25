using FluentAssertions;
using SerialMemory.Core.Interfaces;
using SerialMemory.ML;

namespace SerialMemory.Tests.ML;

public class EmbeddingServiceTests
{
    #region HttpEmbeddingService Tests

    [Fact]
    public void HttpEmbeddingService_Constructor_SetsDefaultDimension()
    {
        // Act
        var service = new HttpEmbeddingService("http://localhost:8765");

        // Assert
        service.EmbeddingDimension.Should().Be(384);
    }

    [Fact]
    public void HttpEmbeddingService_Constructor_AcceptsCustomDimension()
    {
        // Act
        var service = new HttpEmbeddingService("http://localhost:8765", embeddingDimension: 768);

        // Assert
        service.EmbeddingDimension.Should().Be(768);
    }

    [Fact]
    public void HttpEmbeddingService_ImplementsIEmbeddingService()
    {
        // Arrange
        var service = new HttpEmbeddingService("http://localhost:8765");

        // Assert
        service.Should().BeAssignableTo<IEmbeddingService>();
    }

    #endregion

    #region OnnxEmbeddingService Tests

    [Fact]
    public void OnnxEmbeddingService_Constructor_ThrowsForMissingModel()
    {
        // Arrange
        var modelPath = "/nonexistent/model.onnx";
        var vocabPath = "/nonexistent/vocab.txt";

        // Act
        var act = () => new OnnxEmbeddingService(modelPath, vocabPath);

        // Assert
        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*ONNX model not found*");
    }

    [Fact]
    public void OnnxEmbeddingService_Constructor_ThrowsForMissingVocab()
    {
        // Arrange - Create a temp model file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var modelPath = Path.Combine(tempDir, "model.onnx");
        var vocabPath = Path.Combine(tempDir, "vocab.txt");

        try
        {
            // Create a dummy model file
            File.WriteAllBytes(modelPath, new byte[10]);

            // Act
            var act = () => new OnnxEmbeddingService(modelPath, vocabPath);

            // Assert
            act.Should().Throw<FileNotFoundException>()
                .WithMessage("*Vocabulary file not found*");
        }
        finally
        {
            // Cleanup
            Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region EmbeddingServiceFactory Tests

    [Fact]
    public void EmbeddingServiceFactory_Create_ReturnsHttpServiceWhenOnnxNotAvailable()
    {
        // Act
        var service = EmbeddingServiceFactory.Create(
            onnxModelPath: null,
            vocabPath: null,
            httpServiceUrl: "http://localhost:8765");

        // Assert
        service.Should().BeOfType<HttpEmbeddingService>();
    }

    [Fact]
    public void EmbeddingServiceFactory_Create_ReturnsHttpServiceForMissingOnnxFiles()
    {
        // Act
        var service = EmbeddingServiceFactory.Create(
            onnxModelPath: "/nonexistent/model.onnx",
            vocabPath: "/nonexistent/vocab.txt",
            httpServiceUrl: "http://localhost:8765");

        // Assert
        service.Should().BeOfType<HttpEmbeddingService>();
    }

    [Fact]
    public void EmbeddingServiceFactory_Create_UsesDefaultHttpUrlWhenNotProvided()
    {
        // Act
        var service = EmbeddingServiceFactory.Create();

        // Assert
        service.Should().BeOfType<HttpEmbeddingService>();
        service.EmbeddingDimension.Should().Be(384);
    }

    #endregion

    #region Embedding Dimension Tests

    [Theory]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(1024)]
    public void HttpEmbeddingService_EmbeddingDimension_ReturnsConfiguredValue(int dimension)
    {
        // Act
        var service = new HttpEmbeddingService("http://localhost:8765", embeddingDimension: dimension);

        // Assert
        service.EmbeddingDimension.Should().Be(dimension);
    }

    #endregion
}

public class HttpEmbeddingServiceIntegrationTests
{
    // These tests require a running embedding service
    // They are marked with a trait to allow selective running

    [Fact(Skip = "Integration test - requires running embedding service")]
    public async Task EmbedTextAsync_ReturnsEmbeddingOfCorrectDimension()
    {
        // Arrange
        var service = new HttpEmbeddingService("http://localhost:8765");
        var text = "Hello, world!";

        // Act
        var embedding = await service.EmbedTextAsync(text);

        // Assert
        embedding.Should().HaveCount(384);
        embedding.Should().OnlyContain(v => !float.IsNaN(v));
    }

    [Fact(Skip = "Integration test - requires running embedding service")]
    public async Task EmbedBatchAsync_ReturnsEmbeddingsForAllTexts()
    {
        // Arrange
        var service = new HttpEmbeddingService("http://localhost:8765");
        var texts = new List<string> { "Hello", "World", "Test" };

        // Act
        var embeddings = await service.EmbedBatchAsync(texts);

        // Assert
        embeddings.Should().HaveCount(3);
        embeddings.Should().OnlyContain(e => e.Length == 384);
    }

    [Fact(Skip = "Integration test - requires running embedding service")]
    public async Task EmbedTextAsync_SimilarTextsShouldHaveSimilarEmbeddings()
    {
        // Arrange
        var service = new HttpEmbeddingService("http://localhost:8765");
        var text1 = "The quick brown fox";
        var text2 = "A fast brown fox";
        var text3 = "Machine learning algorithms";

        // Act
        var embedding1 = await service.EmbedTextAsync(text1);
        var embedding2 = await service.EmbedTextAsync(text2);
        var embedding3 = await service.EmbedTextAsync(text3);

        // Assert
        var similaritySimilar = CosineSimilarity(embedding1, embedding2);
        var similarityDifferent = CosineSimilarity(embedding1, embedding3);

        similaritySimilar.Should().BeGreaterThan(similarityDifferent);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dotProduct = a.Zip(b, (x, y) => x * y).Sum();
        var magnitudeA = MathF.Sqrt(a.Sum(x => x * x));
        var magnitudeB = MathF.Sqrt(b.Sum(x => x * x));
        return dotProduct / (magnitudeA * magnitudeB);
    }
}
