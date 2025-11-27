using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Integration tests for usage export functionality.
/// </summary>
[Collection("PostgreSQL")]
public class UsageExportTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private ILoggerFactory _loggerFactory = null!;

    public UsageExportTests()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .WithDatabase("testdb")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        _loggerFactory = LoggerFactory.Create(b => b.AddConsole());

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema.sql");
        var schemaV2Path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema_v2.sql");

        if (File.Exists(schemaPath))
        {
            await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));
        }

        if (File.Exists(schemaV2Path))
        {
            await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaV2Path));
        }
    }

    public async Task DisposeAsync()
    {
        _loggerFactory.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ExportToJson_WithEvents_ReturnsValidJson()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "export-json-tenant", "workspace");

        // Create some events
        usageService.TrackUsage(UsageEventType.MemorySearch, latencyMs: 100);
        usageService.TrackUsage(UsageEventType.MemoryIngest, latencyMs: 200);
        usageService.TrackUsage(UsageEventType.MemoryMultiHopSearch, latencyMs: 300);
        await Task.Delay(2500);

        // Act
        var result = await exportService.ExportToJsonAsync("export-json-tenant", "workspace");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("application/json", result.ContentType);
        Assert.NotEmpty(result.Data);
        Assert.True(result.RecordCount >= 3);

        // Verify JSON is valid
        var json = System.Text.Encoding.UTF8.GetString(result.Data);
        var exportData = JsonSerializer.Deserialize<UsageExportData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(exportData);
        Assert.Equal("export-json-tenant", exportData.TenantId);
    }

    [Fact]
    public async Task ExportToCsv_WithEvents_ReturnsValidCsv()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "export-csv-tenant", "workspace");

        // Create some events
        usageService.TrackUsage(UsageEventType.MemorySearch, latencyMs: 100);
        usageService.TrackUsage(UsageEventType.MemoryIngest, latencyMs: 200);
        await Task.Delay(2500);

        // Act
        var result = await exportService.ExportToCsvAsync("export-csv-tenant", "workspace");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("text/csv", result.ContentType);
        Assert.NotEmpty(result.Data);
        Assert.True(result.RecordCount >= 2);

        // Verify CSV has header and data rows
        var csv = System.Text.Encoding.UTF8.GetString(result.Data);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 3); // header + at least 2 data rows
        Assert.Contains("id,tenant_id,workspace_id,event_type", lines[0]);
    }

    [Fact]
    public async Task ExportToJson_WithDateFilter_ReturnsFilteredResults()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "export-filter-tenant", "workspace");

        usageService.TrackUsage(UsageEventType.MemorySearch);
        await Task.Delay(2500);

        // Act - Export with date range that excludes all events
        var result = await exportService.ExportToJsonAsync(
            "export-filter-tenant",
            "workspace",
            fromDate: DateTimeOffset.UtcNow.AddDays(-30),
            toDate: DateTimeOffset.UtcNow.AddDays(-20));

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.RecordCount);
    }

    [Fact]
    public async Task ExportBillingHistory_ReturnsCorrectCycles()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "billing-history-tenant", "workspace");

        // Create usage to generate billing cycle
        usageService.TrackUsage(UsageEventType.MemorySearch);
        await Task.Delay(2500);

        // Act
        var result = await exportService.ExportBillingHistoryAsync("billing-history-tenant", "workspace");

        // Assert
        Assert.True(result.Success);
        Assert.True(result.RecordCount >= 1);

        var json = System.Text.Encoding.UTF8.GetString(result.Data);
        var exportData = JsonSerializer.Deserialize<BillingHistoryExportData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(exportData);
        Assert.NotEmpty(exportData.Cycles);
        Assert.All(exportData.Cycles, c => Assert.True(c.CreditsAllocated > 0));
    }

    [Fact]
    public async Task ExportToJson_EmptyTenant_ReturnsEmptyExport()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());

        // Act
        var result = await exportService.ExportToJsonAsync("non-existent-tenant", "workspace");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.RecordCount);
    }

    [Fact]
    public async Task ExportToCsv_HandlesSpecialCharacters()
    {
        // Arrange
        var exportService = new UsageExportService(_connectionString, _loggerFactory.CreateLogger<UsageExportService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "special-char-tenant", "workspace");

        // Create event with error message containing special chars
        usageService.TrackUsage(
            UsageEventType.MemorySearch,
            success: false,
            errorMessage: "Error with \"quotes\" and, commas");
        await Task.Delay(2500);

        // Act
        var result = await exportService.ExportToCsvAsync("special-char-tenant", "workspace");

        // Assert
        Assert.True(result.Success);

        var csv = System.Text.Encoding.UTF8.GetString(result.Data);
        // Verify the error message is properly escaped
        Assert.Contains("\"\"quotes\"\"", csv);
    }
}
