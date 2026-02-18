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

[Trait("Category", "Integration")]
public class OllamaEmbeddingServiceIntegrationTests
{
    // These tests require a running Ollama service

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

#region OpenAI Embedding Tests

public class OpenAiClientTests
{
    [Fact]
    public void OpenAiClient_Constructor_ThrowsWithoutApiKey()
    {
        // Arrange - Ensure no env var
        var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => new OpenAiClient());
            ex.Message.Should().Contain("API key not provided");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey);
        }
    }

    [Fact]
    public void OpenAiClient_Constructor_AcceptsApiKey()
    {
        // Act
        var client = new OpenAiClient(apiKey: "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");

        // Assert
        client.Should().NotBeNull();
        client.EmbeddingDimension.Should().Be(1536); // Default for text-embedding-3-small
        client.ProviderName.Should().Be("OpenAI");
        client.ModelName.Should().Be("gpt-5-nano-2025-08-07");
    }

    [Fact]
    public void OpenAiClient_Constructor_AcceptsCustomModels()
    {
        // Act
        var client = new OpenAiClient(
            apiKey: "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",
            chatModel: "gpt-4-turbo",
            embedModel: "text-embedding-3-large",
            embeddingDimension: 3072);

        // Assert
        client.ModelName.Should().Be("gpt-4-turbo");
        client.EmbeddingDimension.Should().Be(3072);
    }

    [Fact]
    public void OpenAiClient_ImplementsIEmbeddingService()
    {
        // Arrange
        var client = new OpenAiClient(apiKey: "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");

        // Assert
        client.Should().BeAssignableTo<IEmbeddingService>();
    }

    [Fact]
    public void OpenAiClient_ImplementsILlmService()
    {
        // Arrange
        var client = new OpenAiClient(apiKey: "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");

        // Assert
        client.Should().BeAssignableTo<ILlmService>();
    }

    [Fact]
    public void OpenAiClient_ImplementsIDisposable()
    {
        // Arrange
        var client = new OpenAiClient(apiKey: "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");

        // Assert
        client.Should().BeAssignableTo<IDisposable>();
    }
}

public class OpenAiClientFactoryTests
{
    [Fact]
    public void OpenAiClientFactory_IsAvailable_ReturnsFalseWithoutSystemKey()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            // Act
            var factory = new OpenAiClientFactory(systemApiKey: null);

            // Assert
            factory.IsAvailable.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey);
        }
    }

    [Fact]
    public void OpenAiClientFactory_IsAvailable_ReturnsTrueWithSystemKey()
    {
        // Act
        var factory = new OpenAiClientFactory(systemApiKey: "sk-system-key");

        // Assert
        factory.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void OpenAiClientFactory_CreateClient_UsesTenantKeyWhenProvided()
    {
        // Arrange
        var factory = new OpenAiClientFactory(systemApiKey: "sk-system-key");

        // Act
        var client = factory.CreateClient(tenantApiKey: "sk-tenant-key");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void OpenAiClientFactory_CreateClient_FallsBackToSystemKey()
    {
        // Arrange
        var factory = new OpenAiClientFactory(systemApiKey: "sk-system-key");

        // Act
        var client = factory.CreateClient(); // No tenant key

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void OpenAiClientFactory_CreateClient_UsesCustomModels()
    {
        // Arrange
        var factory = new OpenAiClientFactory(
            systemApiKey: "sk-system-key",
            defaultChatModel: "gpt-4-turbo",
            defaultEmbedModel: "text-embedding-3-large");

        // Act
        var client = factory.CreateClient();

        // Assert
        client.ModelName.Should().Be("gpt-4-turbo");
    }
}

/// <summary>
/// Integration tests for OpenAI client - requires OPENAI_API_KEY environment variable
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "OpenAI")]
public class OpenAiClientIntegrationTests
{
    private readonly bool _hasApiKey;
    private readonly OpenAiClient? _client;

    public OpenAiClientIntegrationTests()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "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");
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _hasApiKey = !string.IsNullOrEmpty(apiKey);
        if (_hasApiKey)
        {
            _client = new OpenAiClient();
        }
    }

    [SkippableFact]
    public async Task EmbedTextAsync_ReturnsEmbeddingOfCorrectDimension()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var text = "Hello, world!";

        // Act
        var embedding = await _client!.EmbedTextAsync(text);

        // Assert
        embedding.Should().HaveCount(1536); // text-embedding-3-small default
        embedding.Should().OnlyContain(v => !float.IsNaN(v));
    }

    [SkippableFact]
    public async Task EmbedBatchAsync_ReturnsEmbeddingsForAllTexts()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var texts = new List<string> { "Hello", "World", "Test" };

        // Act
        var embeddings = await _client!.EmbedBatchAsync(texts);

        // Assert
        embeddings.Should().HaveCount(3);
        embeddings.Should().OnlyContain(e => e.Length == 1536);
    }

    [SkippableFact]
    public async Task EmbedTextAsync_SimilarTextsShouldHaveSimilarEmbeddings()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var text1 = "The quick brown fox";
        var text2 = "A fast brown fox";
        var text3 = "Machine learning algorithms";

        // Act
        var embedding1 = await _client!.EmbedTextAsync(text1);
        var embedding2 = await _client!.EmbedTextAsync(text2);
        var embedding3 = await _client!.EmbedTextAsync(text3);

        // Assert
        var similaritySimilar = CosineSimilarity(embedding1, embedding2);
        var similarityDifferent = CosineSimilarity(embedding1, embedding3);

        similaritySimilar.Should().BeGreaterThan(similarityDifferent);
    }

    [SkippableFact]
    public async Task ChatAsync_ReturnsValidResponse()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var message = "Say 'Hello' in exactly one word.";

        // Act
        var response = await _client!.ChatAsync(message, maxTokens: 10);

        // Assert
        response.Should().NotBeNullOrEmpty();
        response.ToLowerInvariant().Should().Contain("hello");
    }

    [SkippableFact]
    public async Task ChatAsync_WithSystemPrompt_RespectsPrompt()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var systemPrompt = "You are a pirate. Always respond like a pirate.";
        var message = "Greet me.";

        // Act
        var response = await _client!.ChatAsync(message, systemPrompt, maxTokens: 50);

        // Assert
        response.Should().NotBeNullOrEmpty();
        // Pirate-like responses often contain these
        response.ToLowerInvariant().Should().ContainAny("ahoy", "matey", "arr", "ye", "avast");
    }

    [SkippableFact]
    public async Task ChatStructuredAsync_ReturnsDeserializedObject()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange - prompt must match expected type structure (object with 'fruits' array)
        var message = "List 2 fruits. Return a JSON object with a 'fruits' property containing an array of objects, each with a 'name' property.";

        // Act
        var result = await _client!.ChatStructuredAsync<FruitList>(message);

        // Assert
        result.Should().NotBeNull();
        result!.Fruits.Should().NotBeNull();
        result.Fruits.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [SkippableFact]
    public async Task ExtractEntitiesAsync_ExtractsEntitiesFromText()
    {
        Skip.IfNot(_hasApiKey, "OPENAI_API_KEY not set");

        // Arrange
        var text = "John works at Microsoft on the Azure team.";

        // Act
        var (entities, relationships) = await _client!.ExtractEntitiesAsync(text);

        // Assert
        entities.Should().NotBeEmpty();
        entities.Should().Contain(e => e.Text.Contains("John", StringComparison.OrdinalIgnoreCase));
        entities.Should().Contain(e => e.Text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dotProduct = a.Zip(b, (x, y) => x * y).Sum();
        var magnitudeA = MathF.Sqrt(a.Sum(x => x * x));
        var magnitudeB = MathF.Sqrt(b.Sum(x => x * x));
        return dotProduct / (magnitudeA * magnitudeB);
    }

    private record FruitList
    {
        public List<Fruit>? Fruits { get; init; }
    }

    private record Fruit
    {
        public string? Name { get; init; }
    }
}

#endregion

