using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using SerialMemory.Core.Telemetry;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Instrumented database connection that records metrics for all operations.
/// Wraps NpgsqlConnection to automatically capture query timing and counts.
/// </summary>
public sealed class InstrumentedDbConnection(NpgsqlConnection inner, bool ownsConnection = true) : DbConnection
{
    /// <summary>
    /// Gets the underlying NpgsqlConnection for operations that require it directly.
    /// </summary>
    private NpgsqlConnection Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        set => Inner.ConnectionString = value ?? string.Empty;
    }

    public override string Database => Inner.Database;
    public override ConnectionState State => Inner.State;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

    public override void Close()
    {
        Metrics.ActiveDbConnections.Add(-1);
        Inner.Close();
    }

    public override void Open()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Inner.Open();
            Metrics.ActiveDbConnections.Add(1);
        }
        catch (Exception ex)
        {
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            Metrics.RecordDbQuery("connect", "connection", sw.Elapsed.TotalMilliseconds);
        }
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await Inner.OpenAsync(cancellationToken);
            Metrics.ActiveDbConnections.Add(1);
        }
        catch (Exception ex)
        {
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            Metrics.RecordDbQuery("connect", "connection", sw.Elapsed.TotalMilliseconds);
        }
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return Inner.BeginTransaction(isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return new InstrumentedDbCommand(Inner.CreateCommand(), this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsConnection)
        {
            if (Inner.State == ConnectionState.Open)
            {
                Metrics.ActiveDbConnections.Add(-1);
            }
            Inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (ownsConnection)
        {
            if (Inner.State == ConnectionState.Open)
            {
                Metrics.ActiveDbConnections.Add(-1);
            }
            await Inner.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}

/// <summary>
/// Instrumented database command that records metrics for query execution.
/// </summary>
public sealed class InstrumentedDbCommand(NpgsqlCommand inner, InstrumentedDbConnection? connection = null)
    : DbCommand
{
    private readonly NpgsqlCommand _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value ?? string.Empty;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => connection;
        set { /* Not supported */ }
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value as NpgsqlTransaction;
    }

    public override void Cancel() => _inner.Cancel();

    public override int ExecuteNonQuery()
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = _inner.ExecuteNonQuery();
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExecuteNonQueryAsync(cancellationToken);
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    public override object? ExecuteScalar()
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = _inner.ExecuteScalar();
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExecuteScalarAsync(cancellationToken);
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    public override void Prepare() => _inner.Prepare();

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = _inner.ExecuteReader(behavior);
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var (operation, table) = ParseCommandText();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExecuteReaderAsync(behavior, cancellationToken);
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Metrics.RecordDbQuery(operation, table, sw.Elapsed.TotalMilliseconds, success: false);
            Metrics.DbConnectionErrorsTotal.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            throw;
        }
    }

    /// <summary>
    /// Parse the command text to extract operation type and table name for metrics.
    /// </summary>
    private (string Operation, string Table) ParseCommandText()
    {
        var text = CommandText.Trim().ToUpperInvariant();

        // Extract operation type
        var operation = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown";
        operation = operation.ToLower();

        // Extract table name (simplified parsing)
        var table = "unknown";
        var textLower = text.ToLowerInvariant();

        // Try to find FROM clause
        var fromIndex = textLower.IndexOf(" from ", StringComparison.Ordinal);
        if (fromIndex >= 0)
        {
            var afterFrom = textLower[(fromIndex + 6)..].TrimStart();
            table = afterFrom.Split(' ', '\n', '\r', '\t', '(').FirstOrDefault() ?? "unknown";
        }
        // Try to find INTO clause (for INSERT)
        else if (textLower.StartsWith("insert"))
        {
            var intoIndex = textLower.IndexOf(" into ", StringComparison.Ordinal);
            if (intoIndex < 0) return (operation, table.Replace("\"", ""));
            var afterInto = textLower[(intoIndex + 6)..].TrimStart();
            table = afterInto.Split(' ', '\n', '\r', '\t', '(').FirstOrDefault() ?? "unknown";
        }
        // Try to find UPDATE clause
        else if (textLower.StartsWith("update"))
        {
            var afterUpdate = textLower[6..].TrimStart();
            table = afterUpdate.Split(' ', '\n', '\r', '\t').FirstOrDefault() ?? "unknown";
        }
        // Try to find DELETE clause
        else if (textLower.StartsWith("delete"))
        {
            var fromIdx = textLower.IndexOf(" from ", StringComparison.Ordinal);
            if (fromIdx < 0) return (operation, table.Replace("\"", ""));
            var afterFrom = textLower[(fromIdx + 6)..].TrimStart();
            table = afterFrom.Split(' ', '\n', '\r', '\t').FirstOrDefault() ?? "unknown";
        }

        return (operation, table.Replace("\"", ""));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
