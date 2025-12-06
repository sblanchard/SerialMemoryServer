namespace SerialMemory.Infrastructure.Configuration;

/// <summary>
/// Centralized connection string factory for PostgreSQL connections.
/// Eliminates duplicate BuildConnectionString methods across services.
/// </summary>
public static class ConnectionStringFactory
{
    /// <summary>
    /// Default connection string values.
    /// </summary>
    public static class Defaults
    {
        public const string Host = "localhost";
        public const string Port = "5434";
        public const string User = "postgres";
        public const string Password = "postgres";
        public const string Database = "contextdb";
    }

    /// <summary>
    /// Builds a PostgreSQL connection string from environment variables with fallbacks.
    /// </summary>
    /// <returns>A properly formatted PostgreSQL connection string.</returns>
    public static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? Defaults.Host;
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? Defaults.Port;
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? Defaults.User;
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? Defaults.Password;
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? Defaults.Database;

        return $"Host={host};Port={port};Username={user};Password={password};Database={database}";
    }

    /// <summary>
    /// Builds a PostgreSQL connection string with additional options.
    /// </summary>
    /// <param name="options">Optional connection string options to append.</param>
    /// <returns>A properly formatted PostgreSQL connection string with options.</returns>
    public static string BuildConnectionString(ConnectionStringOptions options)
    {
        var baseConnectionString = BuildConnectionString();

        var additionalOptions = new List<string>();

        if (options.Pooling.HasValue)
            additionalOptions.Add($"Pooling={options.Pooling.Value}");

        if (options.MaxPoolSize.HasValue)
            additionalOptions.Add($"Maximum Pool Size={options.MaxPoolSize.Value}");

        if (options.MinPoolSize.HasValue)
            additionalOptions.Add($"Minimum Pool Size={options.MinPoolSize.Value}");

        if (options.CommandTimeout.HasValue)
            additionalOptions.Add($"Command Timeout={options.CommandTimeout.Value}");

        if (options.IncludeErrorDetail.HasValue)
            additionalOptions.Add($"Include Error Detail={options.IncludeErrorDetail.Value}");

        if (additionalOptions.Count == 0)
            return baseConnectionString;

        return $"{baseConnectionString};{string.Join(";", additionalOptions)}";
    }
}

/// <summary>
/// Options for customizing the connection string.
/// </summary>
public sealed class ConnectionStringOptions
{
    public bool? Pooling { get; init; }
    public int? MaxPoolSize { get; init; }
    public int? MinPoolSize { get; init; }
    public int? CommandTimeout { get; init; }
    public bool? IncludeErrorDetail { get; init; }
}
