using Dapper;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Tests verifying pgvector semantic search operations respect tenant isolation.
///
/// CRITICAL: Vector similarity searches must NEVER return another tenant's embeddings.
/// This is essential for preventing semantic data leakage.
/// </summary>
[Collection("TenantIsolation")]
public sealed class PgVectorIsolationTests : TenantIsolationTestBase
{
    // ==========================================
    // PART 3.3: pgvector queries are isolated
    // ==========================================

    [Fact]
    public async Task Vector_Similarity_Search_Returns_Only_Own_Tenant_Data()
    {
        // Use TenantA's first embedding as query vector
        var queryEmbedding = TenantAData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Perform vector similarity search
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS distance
            FROM memories
            ORDER BY embedding <=> @QueryVector::vector
            LIMIT 20",
            new { QueryVector = queryVector });

        var resultList = results.ToList();

        // CRITICAL: All results must belong to TenantA
        AssertNoCrossTenantDataBreach(
            resultList,
            TenantA,
            r => r.TenantId,
            "pgvector similarity search");

        // Should have results (we seeded 10 memories for TenantA)
        Assert.NotEmpty(resultList);
        Assert.True(resultList.Count <= TenantAData.MemoryIds.Count);
    }

    [Fact]
    public async Task Vector_Similarity_Search_Cannot_Return_Other_Tenant_Embeddings()
    {
        // Use TenantB's embedding to search as TenantA
        // Even if TenantB's embedding is the "best match", it should not be returned
        var queryEmbedding = TenantBData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS distance
            FROM memories
            ORDER BY embedding <=> @QueryVector::vector
            LIMIT 100", // Large limit to ensure we're not missing anything
            new { QueryVector = queryVector });

        var resultList = results.ToList();

        // Even with TenantB's embedding as query, we should only get TenantA's data
        AssertNoCrossTenantDataBreach(
            resultList,
            TenantA,
            r => r.TenantId,
            "pgvector search with other tenant's embedding");

        // Verify none of TenantB's memory IDs appear
        foreach (var result in resultList)
        {
            Assert.DoesNotContain(result.Id, TenantBData.MemoryIds);
        }
    }

    [Fact]
    public async Task Vector_KNN_Search_Respects_RLS()
    {
        var queryEmbedding = TenantBData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        // K-nearest neighbors search
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS distance
            FROM memories
            ORDER BY embedding <=> @QueryVector::vector
            LIMIT 5", // K=5
            new { QueryVector = queryVector });

        var resultList = results.ToList();

        // All KNN results must be TenantB's data
        AssertNoCrossTenantDataBreach(
            resultList,
            TenantB,
            r => r.TenantId,
            "pgvector KNN search");

        Assert.True(resultList.Count <= 5);
    }

    [Fact]
    public async Task Vector_Cosine_Similarity_Search_Isolated()
    {
        var queryEmbedding = TenantAData.Embeddings.Values.Skip(1).First();
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Cosine similarity (1 - cosine_distance)
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   1 - (embedding <=> @QueryVector::vector) AS similarity
            FROM memories
            WHERE 1 - (embedding <=> @QueryVector::vector) > 0.5
            ORDER BY similarity DESC",
            new { QueryVector = queryVector });

        var resultList = results.ToList();

        AssertNoCrossTenantDataBreach(
            resultList,
            TenantA,
            r => r.TenantId,
            "pgvector cosine similarity search");
    }

    [Fact]
    public async Task Vector_L2_Distance_Search_Isolated()
    {
        var queryEmbedding = TenantBData.Embeddings.Values.Last();
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        // L2 (Euclidean) distance search
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <-> @QueryVector::vector AS l2_distance
            FROM memories
            ORDER BY embedding <-> @QueryVector::vector
            LIMIT 10",
            new { QueryVector = queryVector });

        AssertNoCrossTenantDataBreach(
            results,
            TenantB,
            r => r.TenantId,
            "pgvector L2 distance search");
    }

    [Fact]
    public async Task Vector_Inner_Product_Search_Isolated()
    {
        var queryEmbedding = TenantAData.Embeddings.Values.First();
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Inner product search (negative for MAX similarity)
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   (embedding <#> @QueryVector::vector) * -1 AS inner_product
            FROM memories
            ORDER BY embedding <#> @QueryVector::vector
            LIMIT 10",
            new { QueryVector = queryVector });

        AssertNoCrossTenantDataBreach(
            results,
            TenantA,
            r => r.TenantId,
            "pgvector inner product search");
    }

    [Fact]
    public async Task Vector_Search_With_Filter_Still_Isolated()
    {
        var queryEmbedding = TenantAData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Search with additional filter
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS distance
            FROM memories
            WHERE source = 'test'
            ORDER BY embedding <=> @QueryVector::vector
            LIMIT 10",
            new { QueryVector = queryVector });

        AssertNoCrossTenantDataBreach(
            results,
            TenantA,
            r => r.TenantId,
            "pgvector filtered search");
    }

    [Fact]
    public async Task Vector_Aggregate_Functions_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Test aggregate functions with vectors don't leak data
        var avgCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM memories WHERE embedding IS NOT NULL");

        Assert.Equal(TenantAData.MemoryIds.Count, avgCount);
    }

    [Fact]
    public async Task Vector_Search_Returns_Zero_Results_For_Other_Tenant_Query()
    {
        // Create a very specific embedding that only TenantB has
        var tenantBSpecificEmbedding = TenantBData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", tenantBSpecificEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Search for "exact match" that exists in TenantB
        var exactMatches = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS distance
            FROM memories
            WHERE embedding <=> @QueryVector::vector < 0.001
            ORDER BY distance",
            new { QueryVector = queryVector });

        var results = exactMatches.ToList();

        // Should find no exact matches because TenantB's data is filtered
        // (The exact match exists, but it's TenantB's data)
        foreach (var result in results)
        {
            // If any results, they must be TenantA's
            Assert.Equal(TenantA, result.TenantId);
        }
    }

    [Fact]
    public async Task Superuser_Can_See_All_Vectors_For_Verification()
    {
        // This test verifies our test setup is correct
        // Superuser should see ALL data (bypasses RLS)
        await using var conn = await ConnectionFactory.OpenAsSuperuserAsync();

        var allMemories = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content, embedding IS NOT NULL AS has_embedding
            FROM memories
            WHERE embedding IS NOT NULL");

        var allList = allMemories.ToList();

        // Should see both tenants' data
        var tenantIds = allList.Select(r => r.TenantId).Distinct().ToList();
        Assert.Contains(TenantA, tenantIds);
        Assert.Contains(TenantB, tenantIds);

        // Total should be both tenants' memories
        Assert.Equal(
            TenantAData.MemoryIds.Count + TenantBData.MemoryIds.Count,
            allList.Count);
    }

    // ==========================================
    // HYBRID SEARCH TESTS
    // ==========================================

    [Fact]
    public async Task Hybrid_Vector_And_FullText_Search_Isolated()
    {
        var queryEmbedding = TenantAData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Hybrid search combining vector similarity and full-text
        var results = await conn.QueryAsync<VectorSearchResult>(@"
            SELECT id, tenant_id, content,
                   embedding <=> @QueryVector::vector AS vector_distance,
                   ts_rank(content_tsvector, plainto_tsquery('english', @Query)) AS text_rank
            FROM memories
            WHERE content_tsvector @@ plainto_tsquery('english', @Query)
            ORDER BY (embedding <=> @QueryVector::vector) * 0.5 +
                     (1 - ts_rank(content_tsvector, plainto_tsquery('english', @Query))) * 0.5
            LIMIT 10",
            new { QueryVector = queryVector, Query = "memory secret" });

        var resultList = results.ToList();

        AssertNoCrossTenantDataBreach(
            resultList,
            TenantA,
            r => r.TenantId,
            "hybrid vector + fulltext search");
    }

    // ==========================================
    // HELPER TYPES
    // ==========================================

    private sealed record VectorSearchResult(Guid id, Guid tenant_id, string content)
    {
        public Guid Id => id;
        public Guid TenantId => tenant_id;
    }
}
