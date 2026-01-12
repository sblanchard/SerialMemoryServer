using MassTransit;
using SerialMemory.Contracts.Events;
using StackExchange.Redis;
using Npgsql;
using Dapper;

namespace SerialMemory.Worker.Consumers;

/// <summary>
/// MassTransit consumer for ContextDeleted events.
/// Handles:
/// - Archiving deleted contexts to PostgreSQL (for audit trail)
/// - Removing active context from Redis cache
/// - Removing from context_snapshots table
/// </summary>
public class ContextDeletedConsumer(WorkerConfiguration config, ILogger<ContextDeletedConsumer> logger)
    : IConsumer<ContextDeleted>
{
    public async Task Consume(ConsumeContext<ContextDeleted> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[MassTransit Consumer] Processing ContextDeleted: Key={Key}, CorrelationId={CorrelationId}, Reason={Reason}",
            message.Key,
            message.CorrelationId,
            message.Reason ?? "not specified");

        try
        {
            // 1. Archive the context value before deletion (for audit trail)
            await ArchiveContextAsync(message);

            // 2. Remove from Redis cache
            await RemoveFromRedisAsync(message.Key);

            // 3. Remove from PostgreSQL context_snapshots
            await RemoveFromPostgreSqlAsync(message.Key);

            logger.LogInformation(
                "[Success] Context deleted and archived: Key={Key}, Source={Source}, Reason={Reason}",
                message.Key,
                message.Source ?? "unknown",
                message.Reason ?? "not specified");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[Error] Failed to process ContextDeleted: Key={Key}, CorrelationId={CorrelationId}",
                message.Key,
                message.CorrelationId);

            // Re-throw to trigger MassTransit retry policy
            throw;
        }
    }

    private async Task ArchiveContextAsync(ContextDeleted message)
    {
        await using var connection = new NpgsqlConnection(config.PostgreSqlConnection);
        await connection.OpenAsync();

        // First, get the current value to archive (if it exists)
        var currentValue = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT data FROM context_snapshots WHERE key = @Key",
            new { Key = message.Key });

        if (currentValue != null)
        {
            // Archive to context_archive table
            await connection.ExecuteAsync(
                """
                INSERT INTO context_archive (id, key, data, deleted_at, deleted_by, reason)
                VALUES (@Id, @Key, @Data, @DeletedAt, @DeletedBy, @Reason)
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    Key = message.Key,
                    Data = currentValue,
                    DeletedAt = message.Timestamp,
                    DeletedBy = message.Source,
                    Reason = message.Reason
                });

            logger.LogDebug("Archived context value for key {Key}", message.Key);
        }
    }

    private async Task RemoveFromRedisAsync(string key)
    {
        try
        {
            var redis = await ConnectionMultiplexer.ConnectAsync(config.RedisConnection);
            var db = redis.GetDatabase();
            var deleted = await db.KeyDeleteAsync($"context:{key}");

            if (deleted)
            {
                logger.LogDebug("Removed key from Redis: context:{Key}", key);
            }
            else
            {
                logger.LogDebug("Key not found in Redis (already deleted): context:{Key}", key);
            }
        }
        catch (RedisConnectionException ex)
        {
            // Log but don't fail - Redis is a cache, not critical
            logger.LogWarning(ex, "Could not connect to Redis to delete key {Key}", key);
        }
    }

    private async Task RemoveFromPostgreSqlAsync(string key)
    {
        await using var connection = new NpgsqlConnection(config.PostgreSqlConnection);
        var rowsAffected = await connection.ExecuteAsync(
            "DELETE FROM context_snapshots WHERE key = @Key",
            new { Key = key });

        logger.LogDebug("Deleted {Rows} row(s) from context_snapshots for key {Key}", rowsAffected, key);
    }
}
