using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Compilation;

public sealed class MemoryCompiler(
    NpgsqlDataSource dataSource,
    IEmbeddingService embeddingService,
    ILogger<MemoryCompiler> logger)
    : IMemoryCompiler
{
    private readonly IEmbeddingService _embeddingService = embeddingService;

    public async Task<CompilationResult> CompileAsync(
        CompilationOptions options,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var taskId = await CreateCompilationTaskAsync("full_compile", cancellationToken);

        try
        {
            progress?.Report(new CompilationProgress(1, 5, "Loading memories with embeddings", 0, 0));

            // Step 1: Load all memories with embeddings
            var memories = await LoadMemoriesWithEmbeddingsAsync(cancellationToken);

            if (memories.Count < options.MinClusterSize)
            {
                logger.LogWarning("Not enough memories ({Count}) for clustering", memories.Count);
                await CompleteCompilationTaskAsync(taskId, 0, cancellationToken);
                return new CompilationResult(0, 0, 0, 0, stopwatch.Elapsed, 0);
            }

            progress?.Report(new CompilationProgress(2, 5, "Computing clusters", 0, memories.Count));

            // Step 2: K-means clustering
            var clusters = await ComputeClustersAsync(
                memories,
                options.MaxClusters,
                options.ClusterRadius,
                options.MinClusterSize,
                progress,
                cancellationToken);

            progress?.Report(new CompilationProgress(3, 5, "Storing clusters", 0, clusters.Count));

            // Step 3: Store clusters
            var (clustersCreated, clustersUpdated) = await StoreClustersAsync(clusters, cancellationToken);

            progress?.Report(new CompilationProgress(4, 5, "Building semantic index", 0, memories.Count));

            // Step 4: Build semantic index
            var indexEntriesCreated = options.RebuildIndexes
                ? await BuildSemanticIndexAsync(memories, clusters, cancellationToken)
                : 0;

            progress?.Report(new CompilationProgress(5, 5, "Finalizing compilation", memories.Count, memories.Count));

            // Step 5: Mark embeddings as compiled
            await MarkEmbeddingsCompiledAsync(memories.Select(m => m.Id).ToList(), cancellationToken);

            var version = await GetCompilationVersionAsync(cancellationToken);
            await CompleteCompilationTaskAsync(taskId, memories.Count, cancellationToken);

            stopwatch.Stop();

            logger.LogInformation(
                "Full compilation completed: {Memories} memories, {ClustersCreated} clusters created, " +
                "{ClustersUpdated} updated, {IndexEntries} index entries in {Duration}ms",
                memories.Count, clustersCreated, clustersUpdated, indexEntriesCreated, stopwatch.ElapsedMilliseconds);

            return new CompilationResult(
                memories.Count,
                clustersCreated,
                clustersUpdated,
                indexEntriesCreated,
                stopwatch.Elapsed,
                version);
        }
        catch (Exception ex)
        {
            await FailCompilationTaskAsync(taskId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task<CompilationResult> IncrementalCompileAsync(
        DateTime since,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var taskId = await CreateCompilationTaskAsync("incremental_compile", cancellationToken);

        try
        {
            progress?.Report(new CompilationProgress(1, 4, "Loading new memories", 0, 0));

            // Load only memories created/updated since the given timestamp
            var newMemories = await LoadMemoriesSinceAsync(since, cancellationToken);

            if (newMemories.Count == 0)
            {
                logger.LogInformation("No new memories to compile since {Since}", since);
                await CompleteCompilationTaskAsync(taskId, 0, cancellationToken);
                return new CompilationResult(0, 0, 0, 0, stopwatch.Elapsed, await GetCompilationVersionAsync(cancellationToken));
            }

            progress?.Report(new CompilationProgress(2, 4, "Assigning to clusters", 0, newMemories.Count));

            // Step 2: Assign new memories to existing clusters
            var existingClusters = await GetClustersAsync(cancellationToken);
            var assignments = AssignToClusters(newMemories, existingClusters.ToList());

            // Identify memories that don't fit existing clusters
            var unassigned = newMemories
                .Where(m => !assignments.ContainsKey(m.Id))
                .ToList();

            int clustersCreated = 0;
            int clustersUpdated = 0;

            // Create new clusters for unassigned memories if enough exist
            if (unassigned.Count >= 5)
            {
                progress?.Report(new CompilationProgress(3, 4, "Creating new clusters", 0, unassigned.Count));

                var newClusters = await ComputeClustersAsync(
                    unassigned,
                    Math.Max(1, unassigned.Count / 5),
                    0.5f,
                    3,
                    null,
                    cancellationToken);

                var (created, updated) = await StoreClustersAsync(newClusters, cancellationToken);
                clustersCreated = created;
                clustersUpdated = updated;
            }

            // Update existing cluster memberships
            await UpdateClusterMembershipsAsync(assignments, cancellationToken);

            progress?.Report(new CompilationProgress(4, 4, "Updating semantic index", 0, newMemories.Count));

            // Update semantic index incrementally
            var indexEntriesCreated = await UpdateSemanticIndexAsync(newMemories, cancellationToken);

            await MarkEmbeddingsCompiledAsync(newMemories.Select(m => m.Id).ToList(), cancellationToken);
            await CompleteCompilationTaskAsync(taskId, newMemories.Count, cancellationToken);

            stopwatch.Stop();

            var version = await GetCompilationVersionAsync(cancellationToken);

            logger.LogInformation(
                "Incremental compilation completed: {Memories} memories, {ClustersCreated} new clusters in {Duration}ms",
                newMemories.Count, clustersCreated, stopwatch.ElapsedMilliseconds);

            return new CompilationResult(
                newMemories.Count,
                clustersCreated,
                clustersUpdated,
                indexEntriesCreated,
                stopwatch.Elapsed,
                version);
        }
        catch (Exception ex)
        {
            await FailCompilationTaskAsync(taskId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<MemoryCluster>> GetClustersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT cluster_id, cluster_name, centroid, cluster_radius, memory_count,
                                              created_at, updated_at, compilation_version, is_stable, parent_cluster_id
                                       FROM memory_clusters
                                       WHERE is_stable = TRUE
                                       ORDER BY memory_count DESC
                           """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        var results = new List<MemoryCluster>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var centroid = reader.GetFieldValue<Vector>(2);
            results.Add(new MemoryCluster(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                centroid.ToArray(),
                reader.GetFloat(3),
                reader.GetInt32(4),
                reader.GetDateTime(5),
                reader.GetDateTime(6),
                reader.GetInt32(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<Guid>> GetClusterMembersAsync(
        Guid clusterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT memory_id
                                       FROM memory_cluster_members
                                       WHERE cluster_id = @ClusterId
                                       ORDER BY distance_to_centroid
                           """;

        var rows = await connection.QueryAsync<Guid>(sql, new { ClusterId = clusterId });
        return rows.ToList();
    }

    public async Task InvalidateClusterAsync(Guid clusterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       UPDATE memory_clusters
                                       SET is_stable = FALSE, updated_at = NOW()
                                       WHERE cluster_id = @ClusterId
                           """;

        await connection.ExecuteAsync(sql, new { ClusterId = clusterId });

        logger.LogInformation("Invalidated cluster {ClusterId}", clusterId);
    }

    private async Task<List<MemoryWithEmbedding>> LoadMemoriesWithEmbeddingsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT id, content, embedding, created_at
                                       FROM memories
                                       WHERE embedding IS NOT NULL
                                       ORDER BY created_at
                           """;

        var results = new List<MemoryWithEmbedding>();

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = reader.GetFieldValue<Vector>(2);
            results.Add(new MemoryWithEmbedding(
                reader.GetGuid(0),
                reader.GetString(1),
                vector.ToArray(),
                reader.GetDateTime(3)));
        }

        return results;
    }

    private async Task<List<MemoryWithEmbedding>> LoadMemoriesSinceAsync(
        DateTime since,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT id, content, embedding, created_at
                                       FROM memories
                                       WHERE embedding IS NOT NULL AND created_at > @Since
                                       ORDER BY created_at
                           """;

        var results = new List<MemoryWithEmbedding>();

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("Since", since);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = reader.GetFieldValue<Vector>(2);
            results.Add(new MemoryWithEmbedding(
                reader.GetGuid(0),
                reader.GetString(1),
                vector.ToArray(),
                reader.GetDateTime(3)));
        }

        return results;
    }

    private async Task<List<ComputedCluster>> ComputeClustersAsync(
        List<MemoryWithEmbedding> memories,
        int maxClusters,
        float maxRadius,
        int minClusterSize,
        IProgress<CompilationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // K-means++ initialization and clustering
        var k = Math.Min(maxClusters, Math.Max(1, memories.Count / minClusterSize));
        var embeddings = memories.Select(m => m.Embedding).ToArray();
        var dimension = embeddings[0].Length;

        // K-means++ initialization
        var centroids = InitializeCentroidsKMeansPlusPlus(embeddings, k);
        var assignments = new int[memories.Count];
        var iterations = 0;
        const int maxIterations = 50;

        while (iterations < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Assignment step
            var changed = false;
            for (int i = 0; i < memories.Count; i++)
            {
                var nearestCluster = FindNearestCentroid(embeddings[i], centroids);
                if (nearestCluster != assignments[i])
                {
                    assignments[i] = nearestCluster;
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

            for (int i = 0; i < memories.Count; i++)
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
                    centroids[c] = newCentroids[c];
                }
            }

            iterations++;
            progress?.Report(new CompilationProgress(2, 5, $"K-means iteration {iterations}/{maxIterations}", iterations, maxIterations));
        }

        // Build cluster results
        var clusters = new List<ComputedCluster>();

        for (int c = 0; c < k; c++)
        {
            var memberIndices = Enumerable.Range(0, memories.Count)
                .Where(i => assignments[i] == c)
                .ToList();

            if (memberIndices.Count < minClusterSize)
            {
                continue;
            }

            var members = memberIndices
                .Select(i => (
                    Memory: memories[i],
                    Distance: CosineDistance(embeddings[i], centroids[c])))
                .ToList();

            var radius = members.Max(m => m.Distance);

            if (radius > maxRadius)
            {
                // Cluster too spread, skip
                continue;
            }

            clusters.Add(new ComputedCluster(
                Guid.CreateVersion7(),
                centroids[c],
                radius,
                members.Select(m => (m.Memory.Id, m.Distance)).ToList()));
        }

        logger.LogDebug(
            "Computed {ClusterCount} clusters from {MemoryCount} memories in {Iterations} iterations",
            clusters.Count, memories.Count, iterations);

        return clusters;
    }

    private static float[][] InitializeCentroidsKMeansPlusPlus(float[][] embeddings, int k)
    {
        var random = new Random(42); // Deterministic seed
        var centroids = new float[k][];
        var n = embeddings.Length;

        // Choose first centroid randomly
        centroids[0] = (float[])embeddings[random.Next(n)].Clone();

        var distances = new float[n];
        for (int c = 1; c < k; c++)
        {
            // Compute distances to nearest centroid
            float sumDistances = 0;
            for (int i = 0; i < n; i++)
            {
                var minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    var dist = CosineDistance(embeddings[i], centroids[j]);
                    minDist = Math.Min(minDist, dist);
                }
                distances[i] = minDist * minDist;
                sumDistances += distances[i];
            }

            // Choose next centroid with probability proportional to D²
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

        return centroids;
    }

    private static int FindNearestCentroid(float[] embedding, float[][] centroids)
    {
        var minDist = float.MaxValue;
        var nearest = 0;

        for (int c = 0; c < centroids.Length; c++)
        {
            var dist = CosineDistance(embedding, centroids[c]);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = c;
            }
        }

        return nearest;
    }

    private static float CosineDistance(float[] a, float[] b)
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

        var similarity = dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        return 1f - similarity;
    }

    private Dictionary<Guid, (Guid ClusterId, float Distance)> AssignToClusters(
        List<MemoryWithEmbedding> memories,
        List<MemoryCluster> clusters)
    {
        var assignments = new Dictionary<Guid, (Guid, float)>();

        foreach (var memory in memories)
        {
            var bestCluster = (Guid?)null;
            var bestDistance = float.MaxValue;

            foreach (var cluster in clusters)
            {
                var distance = CosineDistance(memory.Embedding, cluster.Centroid);
                if (distance < cluster.ClusterRadius && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCluster = cluster.ClusterId;
                }
            }

            if (bestCluster.HasValue)
            {
                assignments[memory.Id] = (bestCluster.Value, bestDistance);
            }
        }

        return assignments;
    }

    private async Task<(int Created, int Updated)> StoreClustersAsync(
        List<ComputedCluster> clusters,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var version = await GetCompilationVersionAsync(cancellationToken) + 1;

        int created = 0, updated = 0;

        foreach (var cluster in clusters)
        {
            // Insert cluster
            const string insertClusterSql = """

                                                            INSERT INTO memory_clusters (
                                                                cluster_id, centroid, cluster_radius, memory_count,
                                                                compilation_version, is_stable
                                                            ) VALUES (
                                                                @ClusterId, @Centroid, @ClusterRadius, @MemoryCount,
                                                                @CompilationVersion, TRUE
                                                            )
                                                            ON CONFLICT (cluster_id) DO UPDATE SET
                                                                centroid = EXCLUDED.centroid,
                                                                cluster_radius = EXCLUDED.cluster_radius,
                                                                memory_count = EXCLUDED.memory_count,
                                                                compilation_version = EXCLUDED.compilation_version,
                                                                updated_at = NOW()
                                                            RETURNING (xmax = 0) as is_insert
                                            """;

            await using var cmd = new NpgsqlCommand(insertClusterSql, connection);
            cmd.Parameters.AddWithValue("ClusterId", cluster.ClusterId);
            cmd.Parameters.AddWithValue("Centroid", new Vector(cluster.Centroid));
            cmd.Parameters.AddWithValue("ClusterRadius", cluster.Radius);
            cmd.Parameters.AddWithValue("MemoryCount", cluster.Members.Count);
            cmd.Parameters.AddWithValue("CompilationVersion", version);

            var isInsert = await cmd.ExecuteScalarAsync(cancellationToken);
            if (isInsert is true)
                created++;
            else
                updated++;

            // Insert cluster members
            const string insertMemberSql = """

                                                           INSERT INTO memory_cluster_members (memory_id, cluster_id, distance_to_centroid, membership_score)
                                                           VALUES (@MemoryId, @ClusterId, @Distance, @MembershipScore)
                                                           ON CONFLICT (memory_id, cluster_id) DO UPDATE SET
                                                               distance_to_centroid = EXCLUDED.distance_to_centroid,
                                                               membership_score = EXCLUDED.membership_score,
                                                               assigned_at = NOW()
                                           """;

            foreach (var (memoryId, distance) in cluster.Members)
            {
                await connection.ExecuteAsync(insertMemberSql, new
                {
                    MemoryId = memoryId,
                    ClusterId = cluster.ClusterId,
                    Distance = distance,
                    MembershipScore = 1f - distance
                });
            }
        }

        return (created, updated);
    }

    private async Task UpdateClusterMembershipsAsync(
        Dictionary<Guid, (Guid ClusterId, float Distance)> assignments,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       INSERT INTO memory_cluster_members (memory_id, cluster_id, distance_to_centroid, membership_score)
                                       VALUES (@MemoryId, @ClusterId, @Distance, @MembershipScore)
                                       ON CONFLICT (memory_id, cluster_id) DO UPDATE SET
                                           distance_to_centroid = EXCLUDED.distance_to_centroid,
                                           membership_score = EXCLUDED.membership_score,
                                           assigned_at = NOW()
                           """;

        foreach (var (memoryId, (clusterId, distance)) in assignments)
        {
            await connection.ExecuteAsync(sql, new
            {
                MemoryId = memoryId,
                ClusterId = clusterId,
                Distance = distance,
                MembershipScore = 1f - distance
            });
        }

        // Update cluster memory counts
        const string updateCountsSql = """

                                                   UPDATE memory_clusters mc
                                                   SET memory_count = (
                                                       SELECT COUNT(*) FROM memory_cluster_members mcm
                                                       WHERE mcm.cluster_id = mc.cluster_id
                                                   ), updated_at = NOW()
                                                   WHERE cluster_id = ANY(@ClusterIds)
                                       """;

        var clusterIds = assignments.Values.Select(a => a.ClusterId).Distinct().ToArray();
        await connection.ExecuteAsync(updateCountsSql, new { ClusterIds = clusterIds });
    }

    private async Task<int> BuildSemanticIndexAsync(
        List<MemoryWithEmbedding> memories,
        List<ComputedCluster> clusters,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Clear existing index
        await connection.ExecuteAsync("TRUNCATE semantic_index");

        var version = await GetCompilationVersionAsync(cancellationToken) + 1;
        int entriesCreated = 0;

        // Build token-based index
        var tokenIndex = new Dictionary<string, List<(Guid MemoryId, float Score)>>();

        foreach (var memory in memories)
        {
            var tokens = TokenizeContent(memory.Content);
            foreach (var token in tokens)
            {
                if (!tokenIndex.TryGetValue(token, out var list))
                {
                    list = new List<(Guid, float)>();
                    tokenIndex[token] = list;
                }
                list.Add((memory.Id, 1.0f / tokens.Count)); // TF-based score
            }
        }

        // Store token index
        const string insertIndexSql = """

                                                  INSERT INTO semantic_index (index_type, key_hash, key_tokens, target_memory_ids, relevance_scores, compilation_version)
                                                  VALUES ('token', @KeyHash, @KeyTokens, @TargetMemoryIds, @RelevanceScores, @CompilationVersion)
                                                  ON CONFLICT (index_type, key_hash) DO UPDATE SET
                                                      target_memory_ids = EXCLUDED.target_memory_ids,
                                                      relevance_scores = EXCLUDED.relevance_scores,
                                                      compilation_version = EXCLUDED.compilation_version
                                      """;

        foreach (var (token, entries) in tokenIndex)
        {
            if (entries.Count < 2) continue; // Skip rare tokens

            var keyHash = ComputeHash(token);
            var memoryIds = entries.Select(e => e.MemoryId).ToArray();
            var scores = entries.Select(e => e.Score).ToArray();

            await connection.ExecuteAsync(insertIndexSql, new
            {
                KeyHash = keyHash,
                KeyTokens = new[] { token },
                TargetMemoryIds = memoryIds,
                RelevanceScores = scores,
                CompilationVersion = version
            });

            entriesCreated++;
        }

        // Build cluster-based index
        foreach (var cluster in clusters)
        {
            var keyHash = ComputeHash($"cluster:{cluster.ClusterId}");
            var memoryIds = cluster.Members.Select(m => m.MemoryId).ToArray();
            var scores = cluster.Members.Select(m => 1f - m.Distance).ToArray();

            await connection.ExecuteAsync(insertIndexSql, new
            {
                KeyHash = keyHash,
                KeyTokens = new[] { $"cluster:{cluster.ClusterId}" },
                TargetMemoryIds = memoryIds,
                RelevanceScores = scores,
                CompilationVersion = version
            });

            entriesCreated++;
        }

        return entriesCreated;
    }

    private async Task<int> UpdateSemanticIndexAsync(
        List<MemoryWithEmbedding> memories,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var version = await GetCompilationVersionAsync(cancellationToken) + 1;
        int entriesUpdated = 0;

        foreach (var memory in memories)
        {
            var tokens = TokenizeContent(memory.Content);

            foreach (var token in tokens.Distinct())
            {
                var keyHash = ComputeHash(token);

                const string updateSql = """

                                                             INSERT INTO semantic_index (index_type, key_hash, key_tokens, target_memory_ids, relevance_scores, compilation_version)
                                                             VALUES ('token', @KeyHash, @KeyTokens, ARRAY[@MemoryId], ARRAY[@Score], @CompilationVersion)
                                                             ON CONFLICT (index_type, key_hash) DO UPDATE SET
                                                                 target_memory_ids = array_append(semantic_index.target_memory_ids, @MemoryId),
                                                                 relevance_scores = array_append(semantic_index.relevance_scores, @Score),
                                                                 compilation_version = @CompilationVersion
                                         """;

                await connection.ExecuteAsync(updateSql, new
                {
                    KeyHash = keyHash,
                    KeyTokens = new[] { token },
                    MemoryId = memory.Id,
                    Score = 1.0f / tokens.Count,
                    CompilationVersion = version
                });

                entriesUpdated++;
            }
        }

        return entriesUpdated;
    }

    private static List<string> TokenizeContent(string content)
    {
        return content.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2 && t.Length < 50)
            .ToList();
    }

    private async Task MarkEmbeddingsCompiledAsync(List<Guid> memoryIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       UPDATE embedding_cache
                                       SET is_compiled = TRUE
                                       WHERE content_hash IN (
                                           SELECT encode(sha256(content::bytea), 'hex')
                                           FROM memories
                                           WHERE id = ANY(@MemoryIds)
                                       )
                           """;

        await connection.ExecuteAsync(sql, new { MemoryIds = memoryIds.ToArray() });
    }

    private async Task<int> GetCompilationVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT COALESCE(MAX(compilation_version), 0)
                                       FROM memory_clusters
                           """;

        return await connection.ExecuteScalarAsync<int>(sql);
    }

    private async Task<Guid> CreateCompilationTaskAsync(string taskType, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var taskId = Guid.CreateVersion7();

        const string sql = """

                                       INSERT INTO compilation_tasks (task_id, task_type, status, started_at)
                                       VALUES (@TaskId, @TaskType, 'in_progress', NOW())
                           """;

        await connection.ExecuteAsync(sql, new { TaskId = taskId, TaskType = taskType });

        return taskId;
    }

    private async Task CompleteCompilationTaskAsync(Guid taskId, int itemsProcessed, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       UPDATE compilation_tasks
                                       SET status = 'completed', completed_at = NOW(), items_processed = @ItemsProcessed
                                       WHERE task_id = @TaskId
                           """;

        await connection.ExecuteAsync(sql, new { TaskId = taskId, ItemsProcessed = itemsProcessed });
    }

    private async Task FailCompilationTaskAsync(Guid taskId, string error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       UPDATE compilation_tasks
                                       SET status = 'failed', completed_at = NOW(), error_message = @Error
                                       WHERE task_id = @TaskId
                           """;

        await connection.ExecuteAsync(sql, new { TaskId = taskId, Error = error });
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private record MemoryWithEmbedding(Guid Id, string Content, float[] Embedding, DateTime CreatedAt);
    private record ComputedCluster(Guid ClusterId, float[] Centroid, float Radius, List<(Guid MemoryId, float Distance)> Members);
}
