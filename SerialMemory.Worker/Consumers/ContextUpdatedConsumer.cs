using MassTransit;
using SerialMemory.Contracts.Events;
using StackExchange.Redis;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Logging;

namespace SerialMemory.Worker.Consumers;

/// <summary>
/// MassTransit consumer for ContextUpdated events.
/// Features:
/// - Automatic retry with exponential backoff (configured at bus level)
/// - Error queue routing for failed messages
/// - Distributed tracing via correlation IDs
/// - Structured logging
/// </summary>
public class ContextUpdatedConsumer(WorkerConfiguration config, ILogger<ContextUpdatedConsumer> logger)
    : IConsumer<ContextUpdated>
{
    public async Task Consume(ConsumeContext<ContextUpdated> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[MassTransit Consumer] Processing ContextUpdated: Key={Key}, CorrelationId={CorrelationId}, MessageId={MessageId}",
            message.Key,
            message.CorrelationId,
            message.MessageId);

        try
        {
            // Read the value from Redis
            var redis = await ConnectionMultiplexer.ConnectAsync(config.RedisConnection);
            var db = redis.GetDatabase();
            var value = await db.StringGetAsync($"context:{message.Key}");

            if (value.HasValue)
            {
                // Persist to PostgreSQL
                await PersistToPostgreSqlAsync(message.Key, value.ToString());

                logger.LogInformation(
                    "[Success] Persisted context to PostgreSQL: Key={Key}, ValueLength={Length}",
                    message.Key,
                    value.ToString().Length);
            }
            else
            {
                logger.LogWarning(
                    "[Warning] Redis key not found: {Key}, skipping persistence",
                    message.Key);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[Error] Failed to process ContextUpdated: Key={Key}, CorrelationId={CorrelationId}",
                message.Key,
                message.CorrelationId);

            // Re-throw to trigger MassTransit retry policy
            throw;
        }
    }

    private async Task PersistToPostgreSqlAsync(string key, string value)
    {
        const string sql = """
            INSERT INTO context_snapshots (key, data, updated_at)
            VALUES (@key, @data, NOW())
            ON CONFLICT (key)
            DO UPDATE SET data = EXCLUDED.data, updated_at = NOW();
            """;

        await using var connection = new NpgsqlConnection(config.PostgreSqlConnection);
        await connection.ExecuteAsync(sql, new { key, data = value });
    }
}
