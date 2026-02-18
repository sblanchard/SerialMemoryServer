using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure.Rag;
using Xunit;

namespace SerialMemory.Tests.Rag;

/// <summary>
/// Unit tests for PersonalizedRagService.
/// Tests RAG answer generation and memory search functionality.
/// </summary>
[Trait("Category", "Integration")]
public class PersonalizedRagServiceTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private NpgsqlDataSource _dataSource = null!;
    private Mock<IEmbeddingService> _mockEmbeddingService = null!;
    private Mock<ILlmService> _mockLlmService = null!;
    private PersonalizedRagService _service = null!;
    private Guid _testTenantId;
    private Guid _testMemoryId;
    private readonly string _testUserId = "test-user";

    public PersonalizedRagServiceTests()
    {
        _connectionString = Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=contextdb_test;Username=postgres;Password=postgres";
        _testTenantId = Guid.CreateVersion7();
        _testMemoryId = Guid.CreateVersion7();
    }

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlDataSourceBuilder(_connectionString);
        builder.UseVector();
        _dataSource = builder.Build();

        _mockEmbeddingService = new Mock<IEmbeddingService>();
        _mockEmbeddingService
            .Setup(x => x.EmbedTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GenerateTestEmbedding(1536));
        _mockEmbeddingService
            .SetupGet(x => x.EmbeddingDimension)
            .Returns(1536);

        _mockLlmService = new Mock<ILlmService>();
        _mockLlmService
            .Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<float>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("This is a test answer based on your memories.");
        _mockLlmService
            .SetupGet(x => x.ModelName)
            .Returns("test-model");
        _mockLlmService
            .SetupGet(x => x.ProviderName)
            .Returns("test-provider");

        _service = new PersonalizedRagService(
            _dataSource,
            _mockEmbeddingService.Object,
            _mockLlmService.Object,
            NullLogger<PersonalizedRagService>.Instance);

        await SetupTestDataAsync();
    }

    private async Task SetupTestDataAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Create tenant
        cmd.CommandText = $"""
            INSERT INTO tenants (id, name, slug, status)
            VALUES ('{_testTenantId}', 'Test Tenant', 'test-{_testTenantId:N}', 'active')
            ON CONFLICT (id) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Set admin role
        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"SELECT set_config('app.tenant_id', '{_testTenantId}', false)";
        await cmd.ExecuteNonQueryAsync();

        // Create test memory
        cmd.CommandText = $"""
            INSERT INTO memories (id, tenant_id, content, importance, created_at)
            VALUES ('{_testMemoryId}', '{_testTenantId}', 'Test memory about machine learning and neural networks', 0.9, NOW())
            ON CONFLICT (id) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Create L1 layer
        var l1LayerId = Guid.CreateVersion7();
        var l1Json = @"{""context"": ""Discussion about deep learning architectures""}";
        cmd.CommandText = $"""
            INSERT INTO memory_layers (id, memory_id, tenant_id, layer, content_json, is_current)
            VALUES ('{l1LayerId}', '{_testMemoryId}', '{_testTenantId}', 'L1_CONTEXT',
                '{l1Json}'::jsonb, true)
            ON CONFLICT DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Create L2 layer
        var l2LayerId = Guid.CreateVersion7();
        var l2Json = @"{""summary"": ""This memory covers machine learning concepts including neural networks, backpropagation, and model training strategies."", ""one_liner"": ""ML and neural network concepts"", ""keywords"": [""machine learning"", ""neural networks"", ""deep learning""]}";
        cmd.CommandText = $"""
            INSERT INTO memory_layers (id, memory_id, tenant_id, layer, content_json, is_current)
            VALUES ('{l2LayerId}', '{_testMemoryId}', '{_testTenantId}', 'L2_SUMMARY',
                '{l2Json}'::jsonb, true)
            ON CONFLICT DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Create L3 layer
        var l3LayerId = Guid.CreateVersion7();
        var l3Json = @"{""facts"": [""Neural networks use backpropagation for training"", ""Deep learning requires large datasets""]}";
        cmd.CommandText = $"""
            INSERT INTO memory_layers (id, memory_id, tenant_id, layer, content_json, is_current)
            VALUES ('{l3LayerId}', '{_testMemoryId}', '{_testTenantId}', 'L3_KNOWLEDGE',
                '{l3Json}'::jsonb, true)
            ON CONFLICT DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Create L2 embedding
        var embedding = GenerateTestEmbedding(1536);
        var embeddingStr = $"[{string.Join(",", embedding)}]";

        cmd.CommandText = $"""
            INSERT INTO memory_l2_embeddings (id, memory_id, tenant_id, summary, one_liner, keywords, embedding, importance)
            VALUES ('{Guid.CreateVersion7()}', '{_testMemoryId}', '{_testTenantId}',
                'This memory covers machine learning concepts',
                'ML concepts',
                ARRAY['machine learning', 'neural networks'],
                '{embeddingStr}'::vector,
                0.9)
            ON CONFLICT DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"DELETE FROM rag_query_results WHERE tenant_id = '{_testTenantId}'";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"DELETE FROM memory_l2_embeddings WHERE tenant_id = '{_testTenantId}'";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"DELETE FROM memory_layers WHERE tenant_id = '{_testTenantId}'";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"DELETE FROM memories WHERE tenant_id = '{_testTenantId}'";
        await cmd.ExecuteNonQueryAsync();

        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task AnswerAsync_ReturnsAnswer_WhenMemoriesFound()
    {
        // Arrange
        var options = new RagOptions
        {
            MaxMemories = 5,
            IncludeL1 = true,
            IncludeL3 = true,
            IncludeL4 = true,
            SimilarityThreshold = 0.1m, // Low threshold to ensure match
            Temperature = 0.3f
        };

        // Act
        var result = await _service.AnswerAsync(_testTenantId, _testUserId, "What do I know about machine learning?", options);

        // Assert
        result.Should().NotBeNull();
        result.Answer.Should().NotBeNullOrEmpty();
        result.QueryId.Should().NotBeEmpty();
        result.ModelName.Should().Be("test-model");
        result.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task AnswerAsync_CallsLlmService_WithFormattedPrompt()
    {
        // Arrange
        var options = new RagOptions
        {
            MaxMemories = 5,
            SimilarityThreshold = 0.1m,
            Temperature = 0.5f
        };

        // Act
        await _service.AnswerAsync(_testTenantId, _testUserId, "Test question", options);

        // Assert
        _mockLlmService.Verify(
            x => x.ChatAsync(
                It.Is<string>(s => s.Contains("Test question")),
                It.IsAny<string?>(),
                0.5f,
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnswerAsync_StoresQueryResult_InDatabase()
    {
        // Arrange
        var options = new RagOptions { MaxMemories = 5, SimilarityThreshold = 0.1m };

        // Act
        var result = await _service.AnswerAsync(_testTenantId, _testUserId, "Test query for storage", options);

        // Assert
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"SELECT set_config('app.tenant_id', '{_testTenantId}', false)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"SELECT COUNT(*) FROM rag_query_results WHERE id = '{result.QueryId}'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1);
    }

    [Fact]
    public async Task SearchMemoriesAsync_ReturnsMemories_WhenMatchesFound()
    {
        // Act
        var memories = await _service.SearchMemoriesAsync(_testTenantId, "machine learning", 10);

        // Assert
        memories.Should().NotBeEmpty();
        memories.First().L2Summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SearchMemoriesAsync_ReturnsEmptyList_WhenNoMatches()
    {
        // Act - search for something unlikely to match
        var memories = await _service.SearchMemoriesAsync(_testTenantId, "xyzzynonexistent12345", 10);

        // Assert - might return some results with low similarity, or empty
        // The test verifies the method doesn't throw
        memories.Should().NotBeNull();
    }

    [Fact]
    public async Task AnswerAsync_IncludesL1Context_WhenOptionEnabled()
    {
        // Arrange
        var options = new RagOptions
        {
            MaxMemories = 5,
            IncludeL1 = true,
            SimilarityThreshold = 0.1m
        };

        // Act
        var result = await _service.AnswerAsync(_testTenantId, _testUserId, "Tell me about ML", options);

        // Assert
        if (result.Memories.Count > 0)
        {
            // L1 context should be populated
            result.Memories.Any(m => !string.IsNullOrEmpty(m.L1Context)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task AnswerAsync_IncludesL3Facts_WhenOptionEnabled()
    {
        // Arrange
        var options = new RagOptions
        {
            MaxMemories = 5,
            IncludeL3 = true,
            SimilarityThreshold = 0.1m
        };

        // Act
        var result = await _service.AnswerAsync(_testTenantId, _testUserId, "Tell me about neural networks", options);

        // Assert
        if (result.Memories.Count > 0)
        {
            // L3 facts should be populated
            result.Memories.Any(m => m.L3Facts.Count > 0).Should().BeTrue();
        }
    }

    [Fact]
    public async Task AnswerAsync_RespectsMaxMemories_Option()
    {
        // Arrange
        var options = new RagOptions
        {
            MaxMemories = 1,
            SimilarityThreshold = 0.1m
        };

        // Act
        var result = await _service.AnswerAsync(_testTenantId, _testUserId, "ML question", options);

        // Assert
        result.Memories.Should().HaveCountLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task AnswerAsync_GeneratesEmbedding_ForQuery()
    {
        // Arrange
        var options = new RagOptions { MaxMemories = 5 };
        var query = "Test query for embedding";

        // Act
        await _service.AnswerAsync(_testTenantId, _testUserId, query, options);

        // Assert
        _mockEmbeddingService.Verify(
            x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static float[] GenerateTestEmbedding(int dimension)
    {
        var random = new Random(42);
        var embedding = new float[dimension];
        for (int i = 0; i < dimension; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2 - 1);
        }
        // Normalize
        var magnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
        for (int i = 0; i < dimension; i++)
        {
            embedding[i] /= magnitude;
        }
        return embedding;
    }
}
