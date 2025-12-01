using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure.Rag;
using Xunit;

namespace SerialMemory.Tests.Rag;

/// <summary>
/// Unit tests for L2EmbeddingService.
/// Tests L2 embedding indexing, retrieval, and reindexing functionality.
/// </summary>
public class L2EmbeddingServiceTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private NpgsqlDataSource _dataSource = null!;
    private Mock<IEmbeddingService> _mockEmbeddingService = null!;
    private L2EmbeddingService _service = null!;
    private Guid _testTenantId;
    private Guid _testMemoryId;

    public L2EmbeddingServiceTests()
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

        _service = new L2EmbeddingService(
            _dataSource,
            _mockEmbeddingService.Object,
            NullLogger<L2EmbeddingService>.Instance);

        // Setup test tenant and memory with L2 layer
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Create tenant
        cmd.CommandText = $"""
            INSERT INTO tenants (id, name, slug, status)
            VALUES ('{_testTenantId}', 'Test Tenant', 'test-{_testTenantId:N}', 'active')
            ON CONFLICT (id) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Set admin role for test setup
        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"SELECT set_config('app.tenant_id', '{_testTenantId}', false)";
        await cmd.ExecuteNonQueryAsync();

        // Create test memory
        cmd.CommandText = $"""
            INSERT INTO memories (id, tenant_id, content, importance)
            VALUES ('{_testMemoryId}', '{_testTenantId}', 'Test memory content for L2 indexing', 0.8)
            ON CONFLICT (id) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();

        // Create L2 layer for the memory
        var l2LayerId = Guid.CreateVersion7();
        var l2Json = @"{""summary"": ""This is a test summary for the memory"", ""one_liner"": ""Test memory summary"", ""keywords"": [""test"", ""memory"", ""summary""]}";
        cmd.CommandText = $"""
            INSERT INTO memory_layers (id, memory_id, tenant_id, layer, content_json, is_current)
            VALUES ('{l2LayerId}', '{_testMemoryId}', '{_testTenantId}', 'L2_SUMMARY',
                '{l2Json}'::jsonb,
                true)
            ON CONFLICT DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        // Cleanup test data
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
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
    public async Task IndexL2Async_CreatesEmbedding_WhenMemoryHasL2Layer()
    {
        // Act
        var embeddingId = await _service.IndexL2Async(_testTenantId, _testMemoryId);

        // Assert
        embeddingId.Should().NotBeEmpty();

        // Verify the embedding was stored
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"SELECT set_config('app.tenant_id', '{_testTenantId}', false)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"SELECT COUNT(*) FROM memory_l2_embeddings WHERE memory_id = '{_testMemoryId}'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1);
    }

    [Fact]
    public async Task IndexL2Async_UpdatesExisting_WhenCalledTwice()
    {
        // Act
        var firstId = await _service.IndexL2Async(_testTenantId, _testMemoryId);
        var secondId = await _service.IndexL2Async(_testTenantId, _testMemoryId);

        // Assert - should be the same ID (upsert)
        firstId.Should().Be(secondId);

        // Verify only one embedding exists
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.role', 'internal_admin', false)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = $"SELECT COUNT(*) FROM memory_l2_embeddings WHERE memory_id = '{_testMemoryId}'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1);
    }

    [Fact]
    public async Task HasL2EmbeddingAsync_ReturnsTrue_WhenEmbeddingExists()
    {
        // Arrange
        await _service.IndexL2Async(_testTenantId, _testMemoryId);

        // Act
        var hasEmbedding = await _service.HasL2EmbeddingAsync(_testTenantId, _testMemoryId);

        // Assert
        hasEmbedding.Should().BeTrue();
    }

    [Fact]
    public async Task HasL2EmbeddingAsync_ReturnsFalse_WhenNoEmbedding()
    {
        // Act
        var hasEmbedding = await _service.HasL2EmbeddingAsync(_testTenantId, Guid.CreateVersion7());

        // Assert
        hasEmbedding.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveL2EmbeddingAsync_DeletesEmbedding()
    {
        // Arrange
        await _service.IndexL2Async(_testTenantId, _testMemoryId);

        // Act
        await _service.RemoveL2EmbeddingAsync(_testTenantId, _testMemoryId);

        // Assert
        var hasEmbedding = await _service.HasL2EmbeddingAsync(_testTenantId, _testMemoryId);
        hasEmbedding.Should().BeFalse();
    }

    [Fact]
    public async Task IndexL2Async_CallsEmbeddingService_WithCorrectContent()
    {
        // Act
        await _service.IndexL2Async(_testTenantId, _testMemoryId);

        // Assert
        _mockEmbeddingService.Verify(
            x => x.EmbedTextAsync(
                It.Is<string>(s => s.Contains("test summary")),
                It.IsAny<CancellationToken>()),
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
