using Npgsql;
using StackExchange.Redis;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for database and cache configuration.
/// </summary>
public static class DatabaseConfiguration
{
    /// <summary>
    /// Adds PostgreSQL database services with pgvector support.
    /// </summary>
    public static IServiceCollection AddPostgresDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = BuildPostgresConnectionString(configuration);

        // Create shared NpgsqlDataSource with vector extension
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        services.AddSingleton(dataSource);
        services.AddSingleton(_ => connectionString); // For services that need raw connection string

        Console.WriteLine($"[INFO] PostgreSQL configured: {GetSafeConnectionInfo(connectionString)}");

        return services;
    }

    /// <summary>
    /// Adds Redis cache services with fallback to in-memory.
    /// </summary>
    public static IServiceCollection AddRedisCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisConnection = configuration.GetConnectionString("Redis") ?? "localhost:6379";

        try
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
            services.AddSingleton<IContextStore, RedisContextStore>();
            Console.WriteLine($"[INFO] Redis connected: {redisConnection}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Redis not available ({ex.Message}), using in-memory context store");
            services.AddSingleton<IContextStore, InMemoryContextStore>();
        }

        return services;
    }

    /// <summary>
    /// Builds PostgreSQL connection string from configuration with environment variable fallbacks.
    /// </summary>
    public static string BuildPostgresConnectionString(IConfiguration configuration)
    {
        return configuration.GetConnectionString("Postgres")
            ?? $"Host={Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost"};" +
               $"Port={Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5434"};" +
               $"Database={Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb"};" +
               $"Username={Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres"};" +
               $"Password={Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres"}";
    }

    /// <summary>
    /// Returns connection info safe for logging (no password).
    /// </summary>
    private static string GetSafeConnectionInfo(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }
}
