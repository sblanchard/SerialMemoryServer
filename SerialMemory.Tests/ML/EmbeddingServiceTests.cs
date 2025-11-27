using FluentAssertions;
using SerialMemory.Core.Interfaces;
using SerialMemory.ML;
using Xunit;

namespace SerialMemory.Tests.ML;

public class EmbeddingServiceTests
{
    #region OllamaEmbeddingService Tests

    [Fact]
    public void OllamaEmbeddingService_Constructor_SetsDefaultValues()
    {
        // Act
        var service = new OllamaEmbeddingService();

        // Assert
        service.EmbeddingDimension.Should().Be(768);
    }

    [Fact]
    public void OllamaEmbeddingService_Constructor_AcceptsCustomValues()
    {
        // Act
        var service = new OllamaEmbeddingService(
            baseUrl: "http://custom:11434",
            model: "custom-model",
            embeddingDimension: 1024);

        // Assert
        service.EmbeddingDimension.Should().Be(1024);
    }

    [Fact]
    public void OllamaEmbeddingService_ImplementsIEmbeddingService()
    {
        // Arrange
        var service = new OllamaEmbeddingService();

        // Assert
        service.Should().BeAssignableTo<IEmbeddingService>();
    }

    [Fact]
    public void OllamaEmbeddingService_ImplementsIDisposable()
    {
        // Arrange
        var service = new OllamaEmbeddingService();

        // Assert
        service.Should().BeAssignableTo<IDisposable>();
    }

    #endregion

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

    #region Embedding Dimension Tests

    [Theory]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(1024)]
    public void OllamaEmbeddingService_EmbeddingDimension_ReturnsConfiguredValue(int dimension)
    {
        // Act
        var service = new OllamaEmbeddingService(embeddingDimension: dimension);

        // Assert
        service.EmbeddingDimension.Should().Be(dimension);
    }

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

public class OllamaEmbeddingServiceIntegrationTests
{
    // These tests require a running Ollama service
    // They are marked with Skip to allow selective running

    [Fact]
    public async Task EmbedTextAsync_ReturnsEmbeddingOfCorrectDimension()
    {
        // Arrange
        var service = new OllamaEmbeddingService();
        var text = "Hello, world!";

        // Act
        var embedding = await service.EmbedTextAsync(text);

        // Assert
        embedding.Should().HaveCount(768);
        embedding.Should().OnlyContain(v => !float.IsNaN(v));
    }

    [Fact]
    public async Task EmbedBatchAsync_ReturnsEmbeddingsForAllTexts()
    {
        // Arrange
        var service = new OllamaEmbeddingService();
        var texts = new List<string> { "Hello", "World", "Test" };

        // Act
        var embeddings = await service.EmbedBatchAsync(texts);

        // Assert
        embeddings.Should().HaveCount(3);
        embeddings.Should().OnlyContain(e => e.Length == 768);
    }

    [Fact]
    public async Task EmbedTextAsync_SimilarTextsShouldHaveSimilarEmbeddings()
    {
        // Arrange
        var service = new OllamaEmbeddingService();
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

