using Dapper;
using Npgsql;

namespace SerialMemory.Api.Analysis;

/// <summary>
/// Repository for storing and retrieving code analysis results.
/// </summary>
public interface IAnalysisResultsRepository
{
    Task<Guid> SaveAnalysisResultAsync(CodeAnalysisResult result, CancellationToken ct = default);
    Task<IReadOnlyList<AnalysisTrace>> GetRecentTracesAsync(int limit = 20, CancellationToken ct = default);
    Task<CodeAnalysisResult?> GetTraceByIdAsync(Guid traceId, CancellationToken ct = default);
    Task<AnalysisStats> GetStatsAsync(CancellationToken ct = default);
}

public sealed class AnalysisResultsRepository : IAnalysisResultsRepository
{
    private readonly string _connectionString;
    private readonly ILogger<AnalysisResultsRepository> _logger;

    public AnalysisResultsRepository(string connectionString, ILogger<AnalysisResultsRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<Guid> SaveAnalysisResultAsync(CodeAnalysisResult result, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Insert the trace
            const string insertTrace = """
                INSERT INTO reasoning_results (
                    id, started_at, completed_at, duration_ms,
                    directory, files_analyzed, findings_count, status
                ) VALUES (
                    @Id, @StartedAt, @CompletedAt, @DurationMs,
                    @Directory, @FilesAnalyzed, @FindingsCount, @Status
                )
                """;

            await conn.ExecuteAsync(insertTrace, new
            {
                Id = result.TraceId,
                result.StartedAt,
                result.CompletedAt,
                result.DurationMs,
                result.Directory,
                result.FilesAnalyzed,
                FindingsCount = result.Findings.Count,
                Status = "completed"
            }, tx);

            // Insert findings
            if (result.Findings.Count > 0)
            {
                const string insertFinding = """
                    INSERT INTO reasoning_findings (
                        id, trace_id, type, severity, title, description,
                        file_path, line_number, code_snippet, confidence, recommendation
                    ) VALUES (
                        @Id, @TraceId, @Type, @Severity, @Title, @Description,
                        @FilePath, @LineNumber, @CodeSnippet, @Confidence, @Recommendation
                    )
                    """;

                foreach (var finding in result.Findings)
                {
                    await conn.ExecuteAsync(insertFinding, new
                    {
                        finding.Id,
                        TraceId = result.TraceId,
                        finding.Type,
                        finding.Severity,
                        finding.Title,
                        finding.Description,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.CodeSnippet,
                        finding.Confidence,
                        finding.Recommendation
                    }, tx);
                }
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("Saved analysis result {TraceId} with {FindingsCount} findings",
                result.TraceId, result.Findings.Count);

            return result.TraceId;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Failed to save analysis result {TraceId}", result.TraceId);
            throw;
        }
    }

    public async Task<IReadOnlyList<AnalysisTrace>> GetRecentTracesAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            SELECT
                r.id AS Id,
                r.started_at AS StartedAt,
                r.completed_at AS CompletedAt,
                r.duration_ms AS DurationMs,
                r.directory AS Directory,
                r.files_analyzed AS FilesAnalyzed,
                r.findings_count AS FindingsCount,
                r.status AS Status,
                COUNT(CASE WHEN f.severity = 'Critical' THEN 1 END) AS CriticalCount,
                COUNT(CASE WHEN f.severity = 'High' THEN 1 END) AS HighCount,
                COUNT(CASE WHEN f.severity = 'Medium' THEN 1 END) AS MediumCount,
                COUNT(CASE WHEN f.severity = 'Low' THEN 1 END) AS LowCount
            FROM reasoning_results r
            LEFT JOIN reasoning_findings f ON f.trace_id = r.id
            GROUP BY r.id, r.started_at, r.completed_at, r.duration_ms, r.directory, r.files_analyzed, r.findings_count, r.status
            ORDER BY r.started_at DESC
            LIMIT @Limit
            """;

        var traces = await conn.QueryAsync<AnalysisTrace>(sql, new { Limit = limit });
        return traces.ToList();
    }

    public async Task<CodeAnalysisResult?> GetTraceByIdAsync(Guid traceId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string traceSql = """
            SELECT
                id AS TraceId,
                started_at AS StartedAt,
                completed_at AS CompletedAt,
                duration_ms AS DurationMs,
                directory AS Directory,
                files_analyzed AS FilesAnalyzed
            FROM reasoning_results
            WHERE id = @TraceId
            """;

        var trace = await conn.QuerySingleOrDefaultAsync<CodeAnalysisResult>(traceSql, new { TraceId = traceId });
        if (trace == null) return null;

        const string findingsSql = """
            SELECT
                id AS Id,
                type AS Type,
                severity AS Severity,
                title AS Title,
                description AS Description,
                file_path AS FilePath,
                line_number AS LineNumber,
                code_snippet AS CodeSnippet,
                confidence AS Confidence,
                recommendation AS Recommendation
            FROM reasoning_findings
            WHERE trace_id = @TraceId
            ORDER BY
                CASE severity
                    WHEN 'Critical' THEN 1
                    WHEN 'High' THEN 2
                    WHEN 'Medium' THEN 3
                    ELSE 4
                END,
                file_path, line_number
            """;

        var findings = await conn.QueryAsync<AnalysisFinding>(findingsSql, new { TraceId = traceId });
        trace.Findings = findings.ToList();

        return trace;
    }

    public async Task<AnalysisStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            SELECT
                COUNT(DISTINCT r.id) AS TotalTraces,
                COALESCE(AVG(r.duration_ms), 0) AS AvgDurationMs,
                COALESCE(SUM(r.findings_count), 0) AS TotalFindings,
                COUNT(CASE WHEN f.severity = 'Critical' THEN 1 END) AS CriticalFindings,
                COUNT(CASE WHEN f.severity = 'High' THEN 1 END) AS HighFindings,
                COUNT(CASE WHEN f.type = 'SQL_INJECTION' THEN 1 END) AS SqlInjectionCount,
                COUNT(CASE WHEN f.type = 'MISSING_TRY_CATCH' THEN 1 END) AS MissingTryCatchCount,
                COUNT(CASE WHEN f.type = 'EF_CORE_N_PLUS_ONE' THEN 1 END) AS NPlusOneCount,
                MAX(r.started_at) AS LastRunAt
            FROM reasoning_results r
            LEFT JOIN reasoning_findings f ON f.trace_id = r.id
            """;

        var stats = await conn.QuerySingleAsync<AnalysisStats>(sql);
        return stats;
    }
}

public class AnalysisTrace
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public string Directory { get; set; } = "";
    public int FilesAnalyzed { get; set; }
    public int FindingsCount { get; set; }
    public string Status { get; set; } = "";
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
}

public class AnalysisStats
{
    public int TotalTraces { get; set; }
    public double AvgDurationMs { get; set; }
    public int TotalFindings { get; set; }
    public int CriticalFindings { get; set; }
    public int HighFindings { get; set; }
    public int SqlInjectionCount { get; set; }
    public int MissingTryCatchCount { get; set; }
    public int NPlusOneCount { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}
