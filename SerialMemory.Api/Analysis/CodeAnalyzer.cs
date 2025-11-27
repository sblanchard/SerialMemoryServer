using System.Text.RegularExpressions;

namespace SerialMemory.Api.Analysis;

/// <summary>
/// Static code analyzer that detects real security and code quality issues.
/// </summary>
public interface ICodeAnalyzer
{
    Task<CodeAnalysisResult> AnalyzeDirectoryAsync(string directory, AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken ct = default);
}

public sealed class CodeAnalyzer : ICodeAnalyzer
{
    private readonly ILogger<CodeAnalyzer> _logger;

    public CodeAnalyzer(ILogger<CodeAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<CodeAnalysisResult> AnalyzeDirectoryAsync(
        string directory,
        AnalysisOptions options,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new CodeAnalysisResult
        {
            TraceId = Guid.CreateVersion7(),
            StartedAt = DateTimeOffset.UtcNow,
            Directory = directory
        };

        var findings = new List<AnalysisFinding>();
        var filesAnalyzed = 0;

        // Get all C# files
        var csFiles = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("/obj/") && !f.Contains("/bin/"))
                .ToArray()
            : [];

        progress?.Report(new AnalysisProgress
        {
            Step = 1,
            StepType = "plan_start",
            Description = $"Found {csFiles.Length} C# files to analyze in {directory}"
        });

        foreach (var file in csFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var relativePath = Path.GetRelativePath(directory, file);

                // Run all detectors
                if (options.DetectSqlInjection)
                {
                    var sqlFindings = DetectSqlInjection(content, relativePath);
                    findings.AddRange(sqlFindings);
                }

                if (options.DetectMissingTryCatch)
                {
                    var tryCatchFindings = DetectMissingTryCatch(content, relativePath);
                    findings.AddRange(tryCatchFindings);
                }

                if (options.DetectEfCoreNPlusOne)
                {
                    var nplusOneFindings = DetectEfCoreNPlusOne(content, relativePath);
                    findings.AddRange(nplusOneFindings);
                }

                filesAnalyzed++;

                if (filesAnalyzed % 10 == 0)
                {
                    progress?.Report(new AnalysisProgress
                    {
                        Step = 2,
                        StepType = "reasoning_step",
                        Description = $"Analyzed {filesAnalyzed}/{csFiles.Length} files, found {findings.Count} issues"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze file: {File}", file);
            }
        }

        progress?.Report(new AnalysisProgress
        {
            Step = 3,
            StepType = "reflection",
            Description = $"Analysis complete: {filesAnalyzed} files, {findings.Count} findings"
        });

        result.FilesAnalyzed = filesAnalyzed;
        result.Findings = findings;
        result.CompletedAt = DateTimeOffset.UtcNow;
        result.DurationMs = (int)(result.CompletedAt.Value - result.StartedAt).TotalMilliseconds;

        return result;
    }

    /// <summary>
    /// Detects potential SQL injection vulnerabilities by finding string concatenation in SQL queries.
    /// </summary>
    private List<AnalysisFinding> DetectSqlInjection(string content, string filePath)
    {
        var findings = new List<AnalysisFinding>();
        var lines = content.Split('\n');

        // Patterns that indicate SQL injection risk
        var dangerousPatterns = new[]
        {
            // String concatenation in SQL
            new Regex(@"(ExecuteSqlRaw|ExecuteSqlInterpolated|FromSqlRaw)\s*\(\s*\$", RegexOptions.IgnoreCase),
            new Regex(@"(ExecuteSqlRaw|FromSqlRaw)\s*\([^)]*\+\s*", RegexOptions.IgnoreCase),
            new Regex(@"""SELECT\s+.*""\s*\+\s*\w+", RegexOptions.IgnoreCase),
            new Regex(@"""INSERT\s+.*""\s*\+\s*\w+", RegexOptions.IgnoreCase),
            new Regex(@"""UPDATE\s+.*""\s*\+\s*\w+", RegexOptions.IgnoreCase),
            new Regex(@"""DELETE\s+.*""\s*\+\s*\w+", RegexOptions.IgnoreCase),
            new Regex(@"SqlCommand\s*\([^)]*\+", RegexOptions.IgnoreCase),
            new Regex(@"new\s+NpgsqlCommand\s*\(\s*\$", RegexOptions.IgnoreCase),
            new Regex(@"\.CommandText\s*=\s*\$", RegexOptions.IgnoreCase),
            new Regex(@"\.CommandText\s*=\s*[^;]*\+", RegexOptions.IgnoreCase),
            // Dapper with string interpolation
            new Regex(@"\.Query(?:Async|Single|First)?\s*[<(][^)]*\$""", RegexOptions.IgnoreCase),
            new Regex(@"\.Execute(?:Async|Scalar)?\s*\(\s*\$""", RegexOptions.IgnoreCase),
        };

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var pattern in dangerousPatterns)
            {
                if (pattern.IsMatch(line))
                {
                    findings.Add(new AnalysisFinding
                    {
                        Id = Guid.CreateVersion7(),
                        Type = "SQL_INJECTION",
                        Severity = "Critical",
                        Title = "Potential SQL Injection Vulnerability",
                        Description = $"String concatenation or interpolation detected in SQL query. Use parameterized queries instead.",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        CodeSnippet = line.Trim(),
                        Confidence = 0.85f,
                        Recommendation = "Use parameterized queries with @parameters or FromSqlInterpolated with FormattableString"
                    });
                    break; // One finding per line
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Detects async methods that don't have proper try-catch for critical operations.
    /// </summary>
    private List<AnalysisFinding> DetectMissingTryCatch(string content, string filePath)
    {
        var findings = new List<AnalysisFinding>();
        var lines = content.Split('\n');

        // Find async methods that perform I/O without try-catch
        var asyncMethodPattern = new Regex(@"async\s+Task.*?\{", RegexOptions.Singleline);
        var ioOperationPatterns = new[]
        {
            new Regex(@"await\s+.*?(Http|File\.|Stream|Socket|Database|Connection)", RegexOptions.IgnoreCase),
            new Regex(@"await\s+.*?\.(ReadAsync|WriteAsync|SendAsync|ReceiveAsync)", RegexOptions.IgnoreCase),
            new Regex(@"await\s+.*?\.(ExecuteAsync|QueryAsync|OpenAsync)", RegexOptions.IgnoreCase),
        };

        // Track method boundaries and try-catch blocks
        var inMethod = false;
        var methodStartLine = 0;
        var braceCount = 0;
        var hasTryCatch = false;
        var hasIoOperation = false;
        var ioOperationLine = 0;
        var ioOperationCode = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Check for async method start
            if (asyncMethodPattern.IsMatch(line) && !inMethod)
            {
                inMethod = true;
                methodStartLine = i + 1;
                braceCount = line.Count(c => c == '{') - line.Count(c => c == '}');
                hasTryCatch = false;
                hasIoOperation = false;
                continue;
            }

            if (inMethod)
            {
                braceCount += line.Count(c => c == '{') - line.Count(c => c == '}');

                // Check for try-catch
                if (line.Contains("try") && (line.Contains("{") || lines.Skip(i + 1).Take(2).Any(l => l.Trim().StartsWith("{"))))
                {
                    hasTryCatch = true;
                }

                // Check for I/O operations
                foreach (var pattern in ioOperationPatterns)
                {
                    if (pattern.IsMatch(line) && !hasIoOperation)
                    {
                        hasIoOperation = true;
                        ioOperationLine = i + 1;
                        ioOperationCode = line.Trim();
                    }
                }

                // End of method
                if (braceCount <= 0)
                {
                    if (hasIoOperation && !hasTryCatch)
                    {
                        findings.Add(new AnalysisFinding
                        {
                            Id = Guid.CreateVersion7(),
                            Type = "MISSING_TRY_CATCH",
                            Severity = "Medium",
                            Title = "Async I/O Operation Without Error Handling",
                            Description = "Async method performs I/O operations without try-catch. Unhandled exceptions may crash the application.",
                            FilePath = filePath,
                            LineNumber = ioOperationLine,
                            CodeSnippet = ioOperationCode,
                            Confidence = 0.7f,
                            Recommendation = "Wrap I/O operations in try-catch blocks with appropriate error handling and logging"
                        });
                    }
                    inMethod = false;
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Detects EF Core N+1 query patterns - loading related entities in a loop.
    /// </summary>
    private List<AnalysisFinding> DetectEfCoreNPlusOne(string content, string filePath)
    {
        var findings = new List<AnalysisFinding>();
        var lines = content.Split('\n');

        // Patterns that indicate N+1 issues
        var loopPatterns = new[]
        {
            new Regex(@"foreach\s*\([^)]+\)", RegexOptions.IgnoreCase),
            new Regex(@"for\s*\([^)]+\)", RegexOptions.IgnoreCase),
            new Regex(@"\.ForEach\s*\(", RegexOptions.IgnoreCase),
            new Regex(@"\.Select\s*\([^)]*=>\s*\{", RegexOptions.IgnoreCase),
        };

        var lazyLoadPatterns = new[]
        {
            // Navigation property access that triggers lazy load
            new Regex(@"\.\w+\.(?:First|Single|Where|Any|Count|ToList|ToArray)\s*\(", RegexOptions.IgnoreCase),
            // Explicit query inside loop
            new Regex(@"await\s+.*?(?:DbContext|_context|_db).*?\.(?:First|Single|Find|Where)", RegexOptions.IgnoreCase),
            new Regex(@"\.Include\s*\([^)]+\).*\.First", RegexOptions.IgnoreCase),
        };

        var inLoop = false;
        var loopStartLine = 0;
        var loopCode = "";
        var braceCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Check for loop start
            foreach (var pattern in loopPatterns)
            {
                if (pattern.IsMatch(line) && !inLoop)
                {
                    inLoop = true;
                    loopStartLine = i + 1;
                    loopCode = line.Trim();
                    braceCount = line.Count(c => c == '{') - line.Count(c => c == '}');
                    if (braceCount == 0 && line.Contains("{"))
                        braceCount = 1;
                    break;
                }
            }

            if (inLoop)
            {
                if (!line.Contains("foreach") && !line.Contains("for "))
                {
                    braceCount += line.Count(c => c == '{') - line.Count(c => c == '}');
                }

                // Check for lazy load patterns inside loop
                foreach (var lazyPattern in lazyLoadPatterns)
                {
                    if (lazyPattern.IsMatch(line))
                    {
                        findings.Add(new AnalysisFinding
                        {
                            Id = Guid.CreateVersion7(),
                            Type = "EF_CORE_N_PLUS_ONE",
                            Severity = "High",
                            Title = "Potential N+1 Query Pattern",
                            Description = "Database query detected inside a loop. This causes N+1 queries and severe performance degradation.",
                            FilePath = filePath,
                            LineNumber = i + 1,
                            CodeSnippet = line.Trim(),
                            Confidence = 0.75f,
                            Recommendation = "Use .Include() or .ThenInclude() to eager load related entities, or use a single query with joins"
                        });
                        break;
                    }
                }

                // End of loop
                if (braceCount <= 0)
                {
                    inLoop = false;
                }
            }
        }

        return findings;
    }
}

public class AnalysisOptions
{
    public bool DetectSqlInjection { get; set; } = true;
    public bool DetectMissingTryCatch { get; set; } = true;
    public bool DetectEfCoreNPlusOne { get; set; } = true;
    public string? ProjectFilter { get; set; }
}

public class AnalysisProgress
{
    public int Step { get; set; }
    public string StepType { get; set; } = "";
    public string Description { get; set; } = "";
}

public class CodeAnalysisResult
{
    public Guid TraceId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public string Directory { get; set; } = "";
    public int FilesAnalyzed { get; set; }
    public List<AnalysisFinding> Findings { get; set; } = [];
}

public class AnalysisFinding
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string CodeSnippet { get; set; } = "";
    public float Confidence { get; set; }
    public string Recommendation { get; set; } = "";
}
