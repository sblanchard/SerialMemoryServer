namespace SerialMemory.Worker;

public class WorkerConfiguration
{
    public required string RedisConnection { get; init; }
    public required string PostgreSqlConnection { get; init; }
    public required string RabbitMqHost { get; init; }
}
