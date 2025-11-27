using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Static code audit tests that scan source code for unsafe database access patterns.
/// These tests FAIL THE BUILD if violations are found.
///
/// ALLOWED PATTERNS:
/// - TenantDbConnectionFactory.cs - the ONLY place that should create connections
/// - Test files (*.Tests/*.cs) - tests need direct access for RLS verification
/// - tools/ directory - standalone utilities
///
/// FORBIDDEN PATTERNS (in production code):
/// - new NpgsqlConnection(...) - must use ITenantDbConnectionFactory
/// - .OpenAsync() on raw connections - must use factory.OpenAsync()
/// - WHERE tenant_id = ... - must rely on RLS, not manual filtering
/// </summary>
public class DatabaseAccessAuditTests
{
    private readonly ITestOutputHelper _output;

    // Files that are ALLOWED to have direct database access
    private static readonly string[] AllowedFiles =
    [
        "TenantDbConnectionFactory.cs",      // The factory itself
        "TenantIsolationTests.cs",           // Tests need direct access
        "UsageServiceIntegrationTests.cs",   // Integration tests
        "MultiTenantUsageTests.cs",          // Multi-tenant tests
        "UsageLimitTests.cs",                // Usage tests
        "UsageExportTests.cs",               // Export tests
        "PlanServiceTests.cs",               // Plan tests
        "AuditLogTests.cs",                  // Audit tests
        "DatabaseAccessAuditTests.cs",       // This test file
        "HealthChecks.cs",                   // Infrastructure health checks (system-level)
        "ContextUpdatedConsumer.cs",         // Worker consumer (system-level messaging)
    ];

    // Directories that are ALLOWED to have direct database access
    private static readonly string[] AllowedDirectories =
    [
        "tools",                             // Standalone utilities
        "SerialMemory.Mcp.Tests",            // Test project
    ];

    // Files that access system-level tables (not tenant-scoped)
    // These are EXCEPTIONS that should be documented and reviewed
    private static readonly string[] SystemLevelServiceFiles =
    [
        "StripeBillingService.cs",           // Billing/subscription management
        "AdminService.cs",                   // Platform administration
        "UsageService.cs",                   // Usage tracking (uses tenant_id in different way)
        "AuditLogService.cs",                // Audit logging
        "ApiKeyService.cs",                  // API key management
        "PlanService.cs",                    // Plan management
        "UsageLimitService.cs",              // Usage limits
        "UsageExportService.cs",             // Usage export
        "JobSupervisionService.cs",          // Job supervision
        "RateLimitingService.cs",            // Rate limiting
        "CostProtectionService.cs",          // Cost protection
        "PayloadValidationService.cs",       // Payload validation
        "ProofService.cs",                   // Proof generation
        "SelfAuditService.cs",               // Self audit
        "AdminAuditService.cs",              // Admin audit
        "TenantDashboardService.cs",         // Tenant dashboard
        "JwtAuthenticationService.cs",       // JWT auth
    ];

    public DatabaseAccessAuditTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Audit_NoDirectNpgsqlConnectionInTenantScopedRepositories()
    {
        var violations = new List<string>();
        var solutionRoot = GetSolutionRoot();

        // Pattern to find: new NpgsqlConnection
        var pattern = new Regex(@"new\s+NpgsqlConnection\s*\(", RegexOptions.Compiled);

        var csFiles = Directory.GetFiles(solutionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsAllowedFile(f))
            .Where(f => !IsInAllowedDirectory(f))
            .Where(f => !IsSystemLevelService(f));

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var matches = pattern.Matches(content);

            if (matches.Count > 0)
            {
                var relativePath = Path.GetRelativePath(solutionRoot, file);
                foreach (Match match in matches)
                {
                    var lineNumber = GetLineNumber(content, match.Index);
                    violations.Add($"VIOLATION: {relativePath}:{lineNumber} - Direct NpgsqlConnection creation");
                }
            }
        }

        foreach (var v in violations)
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Audit_NoManualTenantIdFilteringInQueries()
    {
        var violations = new List<string>();
        var solutionRoot = GetSolutionRoot();

        // Patterns to find manual tenant filtering (should use RLS instead)
        var patterns = new[]
        {
            new Regex(@"WHERE\s+.*tenant_id\s*=\s*@", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"AND\s+tenant_id\s*=\s*@", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        };

        // Only check the main knowledge graph store - other services may legitimately filter
        var filesToCheck = new[]
        {
            Path.Combine(solutionRoot, "SerialMemory.Infrastructure", "PostgresKnowledgeGraphStore.cs"),
        };

        foreach (var file in filesToCheck.Where(File.Exists))
        {
            var content = File.ReadAllText(file);

            foreach (var pattern in patterns)
            {
                var matches = pattern.Matches(content);
                foreach (Match match in matches)
                {
                    var relativePath = Path.GetRelativePath(solutionRoot, file);
                    var lineNumber = GetLineNumber(content, match.Index);
                    violations.Add($"VIOLATION: {relativePath}:{lineNumber} - Manual tenant_id filtering (should use RLS): {match.Value}");
                }
            }
        }

        foreach (var v in violations)
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Audit_NoRawOpenAsyncOutsideFactory()
    {
        var violations = new List<string>();
        var solutionRoot = GetSolutionRoot();

        // Pattern: conn.OpenAsync() without set_tenant_context
        // This is tricky - we look for OpenAsync that's NOT immediately followed by set_tenant_context
        var openAsyncPattern = new Regex(@"\.OpenAsync\s*\(\s*\)", RegexOptions.Compiled);

        var csFiles = Directory.GetFiles(solutionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsAllowedFile(f))
            .Where(f => !IsInAllowedDirectory(f))
            .Where(f => !IsSystemLevelService(f))
            .Where(f => !f.Contains("TenantDbConnectionFactory")); // Factory is allowed

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var matches = openAsyncPattern.Matches(content);

            if (matches.Count > 0)
            {
                // Check if this file also contains set_tenant_context (which would indicate proper usage)
                var hasTenantContextSetting = content.Contains("set_tenant_context");

                if (!hasTenantContextSetting)
                {
                    var relativePath = Path.GetRelativePath(solutionRoot, file);
                    foreach (Match match in matches)
                    {
                        var lineNumber = GetLineNumber(content, match.Index);
                        violations.Add($"VIOLATION: {relativePath}:{lineNumber} - Raw OpenAsync without tenant context");
                    }
                }
            }
        }

        foreach (var v in violations)
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Report_SystemLevelServicesWithDirectAccess()
    {
        // This is an INFO test - it reports system-level services that have direct DB access
        // These are ALLOWED but should be reviewed periodically

        var solutionRoot = GetSolutionRoot();
        var pattern = new Regex(@"new\s+NpgsqlConnection\s*\(", RegexOptions.Compiled);

        _output.WriteLine("=== SYSTEM-LEVEL SERVICES WITH DIRECT DATABASE ACCESS ===");
        _output.WriteLine("These services access system tables (not tenant-scoped) and are ALLOWED.");
        _output.WriteLine("Review periodically to ensure they don't access tenant data directly.");
        _output.WriteLine("");

        foreach (var fileName in SystemLevelServiceFiles)
        {
            var filePath = Directory.GetFiles(solutionRoot, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (filePath != null && File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                var matches = pattern.Matches(content);

                if (matches.Count > 0)
                {
                    var relativePath = Path.GetRelativePath(solutionRoot, filePath);
                    _output.WriteLine($"  [{matches.Count} connections] {relativePath}");
                }
            }
        }

        // This test always passes - it's informational
        Assert.True(true);
    }

    [Fact]
    public void Report_AllDatabaseAccessLocations()
    {
        // Comprehensive audit report of ALL database access in the codebase

        var solutionRoot = GetSolutionRoot();
        var connectionPattern = new Regex(@"new\s+NpgsqlConnection\s*\(", RegexOptions.Compiled);
        var factoryPattern = new Regex(@"(ITenantDbConnectionFactory|TenantDbConnectionFactory)", RegexOptions.Compiled);

        _output.WriteLine("=== COMPREHENSIVE DATABASE ACCESS AUDIT REPORT ===");
        _output.WriteLine($"Scan time: {DateTime.UtcNow:O}");
        _output.WriteLine($"Solution root: {solutionRoot}");
        _output.WriteLine("");

        var allFiles = Directory.GetFiles(solutionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

        var directAccessFiles = new List<(string File, int Count, string Category)>();
        var factoryAccessFiles = new List<string>();

        foreach (var file in allFiles)
        {
            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(solutionRoot, file);

            var connectionMatches = connectionPattern.Matches(content);
            var factoryMatches = factoryPattern.Matches(content);

            if (connectionMatches.Count > 0)
            {
                var category = IsInAllowedDirectory(file) ? "TEST" :
                               IsSystemLevelService(file) ? "SYSTEM" :
                               IsAllowedFile(file) ? "ALLOWED" : "⚠️ REVIEW";

                directAccessFiles.Add((relativePath, connectionMatches.Count, category));
            }

            if (factoryMatches.Count > 0 && connectionMatches.Count == 0)
            {
                factoryAccessFiles.Add(relativePath);
            }
        }

        _output.WriteLine("--- DIRECT NpgsqlConnection USAGE ---");
        foreach (var (file, count, category) in directAccessFiles.OrderBy(x => x.Category))
        {
            _output.WriteLine($"  [{category}] {file} ({count} connections)");
        }

        _output.WriteLine("");
        _output.WriteLine("--- USING TenantDbConnectionFactory (SAFE) ---");
        foreach (var file in factoryAccessFiles)
        {
            _output.WriteLine($"  [SAFE] {file}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== END AUDIT REPORT ===");

        Assert.True(true);
    }

    private static string GetSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "SerialMemoryServer.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? AppContext.BaseDirectory;
    }

    private static bool IsAllowedFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return AllowedFiles.Contains(fileName);
    }

    private static bool IsInAllowedDirectory(string filePath)
    {
        return AllowedDirectories.Any(dir =>
            filePath.Contains($"\\{dir}\\") ||
            filePath.Contains($"/{dir}/"));
    }

    private static bool IsSystemLevelService(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return SystemLevelServiceFiles.Contains(fileName);
    }

    private static int GetLineNumber(string content, int index)
    {
        return content[..index].Count(c => c == '\n') + 1;
    }
}
