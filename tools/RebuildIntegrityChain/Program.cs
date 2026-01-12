// Integrity Chain Rebuild Tool for SerialMemoryServer
// Rebuilds the tamper-evident hash chain for all memories
//
// Usage:
//   dotnet run
//   dotnet run -- --tenant <tenant-id>
//   dotnet run -- --dry-run
//   dotnet run -- --help

using System.Diagnostics;
using Dapper;
using Npgsql;
using SerialMemory.Infrastructure.Integrity;

var cliArgs = args.ToList();
Guid? specificTenant = null;
bool dryRun = false;
bool showHelp = false;
string algorithm = "SHA256";

for (int i = 0; i < cliArgs.Count; i++)
{
    switch (cliArgs[i])
    {
        case "--tenant":
            specificTenant = Guid.Parse(cliArgs[++i]);
            break;
        case "--algorithm":
            algorithm = cliArgs[++i].ToUpperInvariant();
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--help":
        case "-h":
            showHelp = true;
            break;
    }
}

if (showHelp)
{
    Console.WriteLine(@"
Integrity Chain Rebuild Tool for SerialMemoryServer
====================================================

Rebuilds the tamper-evident hash chain for all memories when integrity
verification shows memories as invalid or corrupted.

Usage:
  dotnet run -- [options]

Options:
  --tenant <id>       Only rebuild chain for specific tenant
  --algorithm <alg>   Hash algorithm: SHA256 (default) or BLAKE3
  --dry-run           Show what would be done without making changes
  --help, -h          Show this help message

Environment Variables:
  POSTGRES_HOST       Database host (default: localhost)
  POSTGRES_PORT       Database port (default: 5432)
  POSTGRES_USER       Database user (default: postgres)
  POSTGRES_PASSWORD   Database password (default: postgres)
  POSTGRES_DB         Database name (default: contextdb)
");
    return;
}

// Build connection string from environment
var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
var connectionString = $"Host={host};Port={port};Username={user};Password={password};Database={database}";

Console.WriteLine("=== Integrity Chain Rebuild Tool ===");
Console.WriteLine($"Database: {host}:{port}/{database}");
Console.WriteLine($"Algorithm: {algorithm}");
if (dryRun) Console.WriteLine("Mode: DRY RUN (no changes will be made)");
Console.WriteLine();

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

// Get initial stats
var initialStats = await conn.QueryFirstAsync<dynamic>(@"
    SELECT
        COUNT(CASE WHEN integrity_status = 'valid' THEN 1 END) as valid_count,
        COUNT(CASE WHEN integrity_status = 'invalid' THEN 1 END) as invalid_count,
        COUNT(CASE WHEN integrity_status IS NULL OR integrity_status = 'pending' THEN 1 END) as pending_count
    FROM memories");

Console.WriteLine($"Initial state: {initialStats.valid_count} valid, {initialStats.invalid_count} invalid, {initialStats.pending_count} pending");
Console.WriteLine();

var stopwatch = Stopwatch.StartNew();
int totalProcessed = 0;
int totalUpdated = 0;

// Get tenants to process
List<Guid> tenants;
if (specificTenant.HasValue)
{
    tenants = [specificTenant.Value];
}
else
{
    tenants = (await conn.QueryAsync<Guid>("SELECT DISTINCT tenant_id FROM memories")).ToList();
}

Console.WriteLine($"Processing {tenants.Count} tenant(s)...");
Console.WriteLine();

foreach (var tenantId in tenants)
{
    Console.WriteLine($"Tenant {tenantId}:");

    var memories = (await conn.QueryAsync<MemoryRow>(@"
        SELECT id, tenant_id, content, metadata, source, created_at
        FROM memories
        WHERE tenant_id = @TenantId
        ORDER BY created_at ASC",
        new { TenantId = tenantId })).ToList();

    Console.WriteLine($"  Processing {memories.Count} memories...");

    string previousChainHash = "GENESIS";
    int updated = 0;

    foreach (var memory in memories)
    {
        var canonicalForm = CryptoUtilities.CreateCanonicalForm(
            memory.tenant_id,
            memory.id,
            memory.created_at,
            memory.content,
            memory.metadata,
            memory.source);

        var canonicalString = CryptoUtilities.SerializeCanonicalForm(canonicalForm);
        var contentHash = CryptoUtilities.ComputeHash(canonicalString, algorithm);
        var chainHash = CryptoUtilities.ComputeChainHash(previousChainHash, contentHash, algorithm);

        if (!dryRun)
        {
            await conn.ExecuteAsync(@"
                UPDATE memories
                SET content_hash = @ContentHash,
                    chain_hash = @ChainHash,
                    integrity_status = 'valid'
                WHERE id = @Id",
                new { Id = memory.id, ContentHash = contentHash, ChainHash = chainHash });
        }

        previousChainHash = chainHash;
        updated++;

        if (updated % 100 == 0)
            Console.WriteLine($"    Updated {updated}/{memories.Count}...");
    }

    totalProcessed += memories.Count;
    totalUpdated += updated;
    Console.WriteLine($"  Completed: {updated} memories updated");
}

stopwatch.Stop();

// Get final stats
var finalStats = await conn.QueryFirstAsync<dynamic>(@"
    SELECT
        COUNT(CASE WHEN integrity_status = 'valid' THEN 1 END) as valid_count,
        COUNT(CASE WHEN integrity_status = 'invalid' THEN 1 END) as invalid_count,
        COUNT(CASE WHEN integrity_status IS NULL OR integrity_status = 'pending' THEN 1 END) as pending_count
    FROM memories");

Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Duration: {stopwatch.Elapsed:mm\\:ss\\.fff}");
Console.WriteLine($"Total processed: {totalProcessed}");
Console.WriteLine($"Total updated: {totalUpdated}");
Console.WriteLine();
Console.WriteLine($"Before: {initialStats.valid_count} valid, {initialStats.invalid_count} invalid, {initialStats.pending_count} pending");
Console.WriteLine($"After:  {finalStats.valid_count} valid, {finalStats.invalid_count} invalid, {finalStats.pending_count} pending");

if (dryRun)
{
    Console.WriteLine();
    Console.WriteLine("This was a DRY RUN. No changes were made.");
}
else if ((long)finalStats.invalid_count == 0 && (long)finalStats.pending_count == 0)
{
    Console.WriteLine();
    Console.WriteLine("✅ All memories now have valid integrity hashes!");
}

class MemoryRow
{
    public Guid id { get; set; }
    public Guid tenant_id { get; set; }
    public string content { get; set; } = "";
    public string? metadata { get; set; }
    public string? source { get; set; }
    public DateTimeOffset created_at { get; set; }
}
