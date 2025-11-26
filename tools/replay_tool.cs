#!/usr/bin/env dotnet-script
// Replay Tool for Deterministic Memory Engine
// Usage: dotnet script replay_tool.cs -- [command] [args]

#r "nuget: Npgsql, 8.0.0"
#r "nuget: Dapper, 2.1.0"
#r "nuget: System.Text.Json, 8.0.0"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

// Configuration
var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION") ??
    "Host=localhost;Port=5432;Database=contextdb;Username=postgres;Password=postgres";

// Command handlers
var commands = new Dictionary<string, Func<string[], Task>>
{
    ["list-sessions"] = ListSessionsAsync,
    ["show-session"] = ShowSessionAsync,
    ["replay-session"] = ReplaySessionAsync,
    ["verify-replay"] = VerifyReplayAsync,
    ["list-snapshots"] = ListSnapshotsAsync,
    ["show-snapshot"] = ShowSnapshotAsync,
    ["compare-snapshots"] = CompareSnapshotsAsync,
    ["memory-history"] = MemoryHistoryAsync,
    ["time-travel"] = TimeTravelAsync,
    ["help"] = HelpAsync
};

// Main entry point
var args = Args.ToArray();
if (args.Length == 0 || !commands.ContainsKey(args[0]))
{
    await HelpAsync(args);
    return;
}

await commands[args[0]](args.Skip(1).ToArray());

// Command implementations

async Task ListSessionsAsync(string[] args)
{
    Console.WriteLine("=== Inference Sessions ===\n");

    await using var connection = new NpgsqlConnection(connectionString);

    var sql = @"
        SELECT session_id, seed, status, created_at, completed_at,
               (SELECT COUNT(*) FROM reasoning_steps WHERE session_id = s.session_id) as step_count
        FROM inference_sessions s
        ORDER BY created_at DESC
        LIMIT 20";

    var sessions = await connection.QueryAsync<dynamic>(sql);

    Console.WriteLine($"{"Session ID",-38} {"Seed",-15} {"Status",-12} {"Steps",-6} {"Created"}");
    Console.WriteLine(new string('-', 100));

    foreach (var session in sessions)
    {
        Console.WriteLine($"{session.session_id,-38} {session.seed,-15} {session.status,-12} {session.step_count,-6} {session.created_at:yyyy-MM-dd HH:mm:ss}");
    }
}

async Task ShowSessionAsync(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: show-session <session-id>");
        return;
    }

    var sessionId = Guid.Parse(args[0]);

    await using var connection = new NpgsqlConnection(connectionString);

    // Get session details
    var sessionSql = @"
        SELECT session_id, seed, session_hash, input_hash, output_hash,
               config_snapshot::text, status, created_at, completed_at,
               parent_session_id, replay_source_session_id
        FROM inference_sessions
        WHERE session_id = @SessionId";

    var session = await connection.QuerySingleOrDefaultAsync<dynamic>(sessionSql, new { SessionId = sessionId });

    if (session == null)
    {
        Console.WriteLine($"Session {sessionId} not found");
        return;
    }

    Console.WriteLine("=== Session Details ===\n");
    Console.WriteLine($"Session ID:     {session.session_id}");
    Console.WriteLine($"Seed:           {session.seed}");
    Console.WriteLine($"Session Hash:   {session.session_hash}");
    Console.WriteLine($"Input Hash:     {session.input_hash}");
    Console.WriteLine($"Output Hash:    {session.output_hash ?? "N/A"}");
    Console.WriteLine($"Status:         {session.status}");
    Console.WriteLine($"Created:        {session.created_at:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Completed:      {session.completed_at?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
    Console.WriteLine($"Parent:         {session.parent_session_id?.ToString() ?? "N/A"}");
    Console.WriteLine($"Replay Source:  {session.replay_source_session_id?.ToString() ?? "N/A"}");

    // Get reasoning steps
    var stepsSql = @"
        SELECT step_sequence, step_type, input_hash, output_hash, duration_ms, created_at
        FROM reasoning_steps
        WHERE session_id = @SessionId
        ORDER BY step_sequence";

    var steps = await connection.QueryAsync<dynamic>(stepsSql, new { SessionId = sessionId });

    Console.WriteLine("\n=== Reasoning Steps ===\n");
    Console.WriteLine($"{"#",-4} {"Type",-25} {"Duration",-10} {"Input Hash",-20} {"Output Hash",-20}");
    Console.WriteLine(new string('-', 80));

    foreach (var step in steps)
    {
        Console.WriteLine($"{step.step_sequence,-4} {step.step_type,-25} {step.duration_ms + "ms",-10} {step.input_hash.Substring(0, 16)}... {step.output_hash.Substring(0, 16)}...");
    }
}

async Task ReplaySessionAsync(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: replay-session <source-session-id> [--seed <override-seed>]");
        return;
    }

    var sourceSessionId = Guid.Parse(args[0]);
    long? overrideSeed = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--seed" && i + 1 < args.Length)
        {
            overrideSeed = long.Parse(args[++i]);
        }
    }

    await using var connection = new NpgsqlConnection(connectionString);

    // Get source session
    var sourceSql = @"
        SELECT seed, config_snapshot::text
        FROM inference_sessions
        WHERE session_id = @SessionId";

    var source = await connection.QuerySingleOrDefaultAsync<dynamic>(sourceSql, new { SessionId = sourceSessionId });

    if (source == null)
    {
        Console.WriteLine($"Source session {sourceSessionId} not found");
        return;
    }

    var actualSeed = overrideSeed ?? (long)source.seed;
    var replaySessionId = Guid.NewGuid();

    Console.WriteLine($"Creating replay session {replaySessionId}");
    Console.WriteLine($"  Source: {sourceSessionId}");
    Console.WriteLine($"  Seed: {actualSeed}" + (overrideSeed.HasValue ? " (overridden)" : ""));

    // Create replay session
    var createSql = @"
        INSERT INTO inference_sessions (
            session_id, seed, session_hash, input_hash, config_snapshot,
            status, replay_source_session_id
        ) VALUES (
            @SessionId, @Seed, @SessionHash, @InputHash, @ConfigSnapshot::jsonb,
            'replaying', @SourceSessionId
        )";

    var sessionHash = ComputeHash($"{replaySessionId}:{actualSeed}");
    var inputHash = ComputeHash($"{actualSeed}:{source.config_snapshot}");

    await connection.ExecuteAsync(createSql, new
    {
        SessionId = replaySessionId,
        Seed = actualSeed,
        SessionHash = sessionHash,
        InputHash = inputHash,
        ConfigSnapshot = source.config_snapshot,
        SourceSessionId = sourceSessionId
    });

    // Get source steps
    var stepsSql = @"
        SELECT step_sequence, step_type, input_data::text, output_data::text
        FROM reasoning_steps
        WHERE session_id = @SessionId
        ORDER BY step_sequence";

    var sourceSteps = (await connection.QueryAsync<dynamic>(stepsSql, new { SessionId = sourceSessionId })).ToList();

    Console.WriteLine($"\nReplaying {sourceSteps.Count} steps...\n");

    var startTime = DateTime.UtcNow;
    var random = new Random((int)actualSeed);

    foreach (var step in sourceSteps)
    {
        var stepStart = DateTime.UtcNow;

        // Simulate step execution (in real implementation, would call actual executor)
        await Task.Delay(1); // Minimal delay

        var stepDuration = (int)(DateTime.UtcNow - stepStart).TotalMilliseconds;

        // Store replayed step
        var insertStepSql = @"
            INSERT INTO reasoning_steps (
                session_id, step_sequence, step_type, input_hash, output_hash,
                input_data, output_data, duration_ms
            ) VALUES (
                @SessionId, @StepSequence, @StepType, @InputHash, @OutputHash,
                @InputData::jsonb, @OutputData::jsonb, @DurationMs
            )";

        await connection.ExecuteAsync(insertStepSql, new
        {
            SessionId = replaySessionId,
            StepSequence = step.step_sequence,
            StepType = step.step_type,
            InputHash = ComputeHash(step.input_data),
            OutputHash = ComputeHash(step.output_data),
            InputData = step.input_data,
            OutputData = step.output_data,
            DurationMs = stepDuration
        });

        Console.WriteLine($"  Step {step.step_sequence}: {step.step_type} ({stepDuration}ms)");
    }

    // Complete replay session
    var completeSql = @"
        UPDATE inference_sessions
        SET status = 'completed', completed_at = NOW()
        WHERE session_id = @SessionId";

    await connection.ExecuteAsync(completeSql, new { SessionId = replaySessionId });

    var totalTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
    Console.WriteLine($"\nReplay completed in {totalTime:F0}ms");
    Console.WriteLine($"Replay session ID: {replaySessionId}");
}

async Task VerifyReplayAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: verify-replay <original-session-id> <replay-session-id>");
        return;
    }

    var originalId = Guid.Parse(args[0]);
    var replayId = Guid.Parse(args[1]);

    await using var connection = new NpgsqlConnection(connectionString);

    var stepsSql = @"
        SELECT step_sequence, step_type, output_hash
        FROM reasoning_steps
        WHERE session_id = @SessionId
        ORDER BY step_sequence";

    var originalSteps = (await connection.QueryAsync<dynamic>(stepsSql, new { SessionId = originalId })).ToList();
    var replaySteps = (await connection.QueryAsync<dynamic>(stepsSql, new { SessionId = replayId })).ToList();

    Console.WriteLine("=== Replay Verification ===\n");
    Console.WriteLine($"Original session: {originalId}");
    Console.WriteLine($"Replay session:   {replayId}");
    Console.WriteLine($"Original steps:   {originalSteps.Count}");
    Console.WriteLine($"Replay steps:     {replaySteps.Count}");

    var divergences = new List<(int Step, string Type, string OrigHash, string ReplayHash)>();
    var minSteps = Math.Min(originalSteps.Count, replaySteps.Count);

    for (int i = 0; i < minSteps; i++)
    {
        var orig = originalSteps[i];
        var replay = replaySteps[i];

        if (orig.output_hash != replay.output_hash)
        {
            divergences.Add((orig.step_sequence, orig.step_type, orig.output_hash, replay.output_hash));
        }
    }

    Console.WriteLine($"\n{"Step",-6} {"Type",-25} {"Status",-15}");
    Console.WriteLine(new string('-', 50));

    for (int i = 0; i < minSteps; i++)
    {
        var orig = originalSteps[i];
        var isDivergent = divergences.Any(d => d.Step == orig.step_sequence);
        var status = isDivergent ? "DIVERGED" : "OK";
        Console.WriteLine($"{orig.step_sequence,-6} {orig.step_type,-25} {status,-15}");
    }

    Console.WriteLine($"\n=== Verification Result ===");
    Console.WriteLine($"Total steps compared: {minSteps}");
    Console.WriteLine($"Divergences found:    {divergences.Count}");
    Console.WriteLine($"Is deterministic:     {(divergences.Count == 0 ? "YES" : "NO")}");

    if (divergences.Count > 0)
    {
        Console.WriteLine("\nDivergence details:");
        foreach (var (step, type, origHash, replayHash) in divergences)
        {
            Console.WriteLine($"  Step {step} ({type}):");
            Console.WriteLine($"    Original: {origHash.Substring(0, 32)}...");
            Console.WriteLine($"    Replay:   {replayHash.Substring(0, 32)}...");
        }
    }
}

async Task ListSnapshotsAsync(string[] args)
{
    Console.WriteLine("=== Memory Snapshots ===\n");

    await using var connection = new NpgsqlConnection(connectionString);

    var sql = @"
        SELECT snapshot_id, snapshot_timestamp, snapshot_type,
               memory_count, entity_count, relationship_count, cluster_count,
               checkpoint_hash
        FROM memory_snapshots
        ORDER BY snapshot_timestamp DESC
        LIMIT 20";

    var snapshots = await connection.QueryAsync<dynamic>(sql);

    Console.WriteLine($"{"Snapshot ID",-38} {"Type",-15} {"Timestamp",-20} {"Memories",-10} {"Entities",-10} {"Rels",-8}");
    Console.WriteLine(new string('-', 110));

    foreach (var snapshot in snapshots)
    {
        Console.WriteLine($"{snapshot.snapshot_id,-38} {snapshot.snapshot_type,-15} {snapshot.snapshot_timestamp:yyyy-MM-dd HH:mm:ss} {snapshot.memory_count,-10} {snapshot.entity_count,-10} {snapshot.relationship_count,-8}");
    }
}

async Task ShowSnapshotAsync(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: show-snapshot <snapshot-id>");
        return;
    }

    var snapshotId = Guid.Parse(args[0]);

    await using var connection = new NpgsqlConnection(connectionString);

    var sql = @"
        SELECT snapshot_id, snapshot_timestamp, snapshot_type,
               memory_count, entity_count, relationship_count, cluster_count,
               checkpoint_hash, created_at, metadata::text
        FROM memory_snapshots
        WHERE snapshot_id = @SnapshotId";

    var snapshot = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new { SnapshotId = snapshotId });

    if (snapshot == null)
    {
        Console.WriteLine($"Snapshot {snapshotId} not found");
        return;
    }

    Console.WriteLine("=== Snapshot Details ===\n");
    Console.WriteLine($"Snapshot ID:     {snapshot.snapshot_id}");
    Console.WriteLine($"Timestamp:       {snapshot.snapshot_timestamp:yyyy-MM-dd HH:mm:ss.fff}");
    Console.WriteLine($"Type:            {snapshot.snapshot_type}");
    Console.WriteLine($"Checkpoint Hash: {snapshot.checkpoint_hash}");
    Console.WriteLine($"Created:         {snapshot.created_at:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"\nStatistics:");
    Console.WriteLine($"  Memories:      {snapshot.memory_count}");
    Console.WriteLine($"  Entities:      {snapshot.entity_count}");
    Console.WriteLine($"  Relationships: {snapshot.relationship_count}");
    Console.WriteLine($"  Clusters:      {snapshot.cluster_count}");
}

async Task CompareSnapshotsAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: compare-snapshots <snapshot-id-a> <snapshot-id-b>");
        return;
    }

    var snapshotIdA = Guid.Parse(args[0]);
    var snapshotIdB = Guid.Parse(args[1]);

    await using var connection = new NpgsqlConnection(connectionString);

    var sql = @"
        SELECT snapshot_id, snapshot_timestamp, snapshot_type,
               memory_count, entity_count, relationship_count, cluster_count
        FROM memory_snapshots
        WHERE snapshot_id IN (@SnapshotIdA, @SnapshotIdB)
        ORDER BY snapshot_timestamp";

    var snapshots = (await connection.QueryAsync<dynamic>(sql, new { SnapshotIdA = snapshotIdA, SnapshotIdB = snapshotIdB })).ToList();

    if (snapshots.Count < 2)
    {
        Console.WriteLine("One or both snapshots not found");
        return;
    }

    var a = snapshots[0];
    var b = snapshots[1];

    Console.WriteLine("=== Snapshot Comparison ===\n");
    Console.WriteLine($"{"Metric",-20} {"Snapshot A",-15} {"Snapshot B",-15} {"Change",-15}");
    Console.WriteLine(new string('-', 70));

    Console.WriteLine($"{"Timestamp",-20} {a.snapshot_timestamp:MM-dd HH:mm,-15} {b.snapshot_timestamp:MM-dd HH:mm,-15} {(b.snapshot_timestamp - a.snapshot_timestamp).TotalHours:+0.0;-0.0} hours");
    Console.WriteLine($"{"Memories",-20} {a.memory_count,-15} {b.memory_count,-15} {b.memory_count - a.memory_count:+0;-0}");
    Console.WriteLine($"{"Entities",-20} {a.entity_count,-15} {b.entity_count,-15} {b.entity_count - a.entity_count:+0;-0}");
    Console.WriteLine($"{"Relationships",-20} {a.relationship_count,-15} {b.relationship_count,-15} {b.relationship_count - a.relationship_count:+0;-0}");
    Console.WriteLine($"{"Clusters",-20} {a.cluster_count,-15} {b.cluster_count,-15} {b.cluster_count - a.cluster_count:+0;-0}");
}

async Task MemoryHistoryAsync(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: memory-history <memory-id>");
        return;
    }

    var memoryId = Guid.Parse(args[0]);

    await using var connection = new NpgsqlConnection(connectionString);

    // Get memory details
    var memorySql = @"
        SELECT id, content, created_at, source
        FROM memories
        WHERE id = @MemoryId";

    var memory = await connection.QuerySingleOrDefaultAsync<dynamic>(memorySql, new { MemoryId = memoryId });

    if (memory == null)
    {
        Console.WriteLine($"Memory {memoryId} not found");
        return;
    }

    Console.WriteLine("=== Memory History ===\n");
    Console.WriteLine($"Memory ID: {memory.id}");
    Console.WriteLine($"Created:   {memory.created_at:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Source:    {memory.source ?? "N/A"}");
    Console.WriteLine($"Content:   {(memory.content.Length > 100 ? memory.content.Substring(0, 100) + "..." : memory.content)}");

    // Get timeline states
    var timelineSql = @"
        SELECT state_at, confidence, layer, is_active
        FROM memory_state_timeline
        WHERE memory_id = @MemoryId
        ORDER BY state_at";

    var states = await connection.QueryAsync<dynamic>(timelineSql, new { MemoryId = memoryId });

    Console.WriteLine("\n=== State Timeline ===\n");
    Console.WriteLine($"{"Timestamp",-25} {"Layer",-15} {"Confidence",-12} {"Active",-8}");
    Console.WriteLine(new string('-', 65));

    foreach (var state in states)
    {
        Console.WriteLine($"{state.state_at:yyyy-MM-dd HH:mm:ss.fff,-25} {state.layer,-15} {state.confidence:F3,-12} {(state.is_active ? "Yes" : "No"),-8}");
    }

    // Get events
    var eventsSql = @"
        SELECT event_type::text, event_version, created_at
        FROM memory_events
        WHERE stream_id = @MemoryId
        ORDER BY event_version";

    var events = await connection.QueryAsync<dynamic>(eventsSql, new { MemoryId = memoryId });

    if (events.Any())
    {
        Console.WriteLine("\n=== Events ===\n");
        Console.WriteLine($"{"Version",-10} {"Event Type",-30} {"Timestamp",-25}");
        Console.WriteLine(new string('-', 70));

        foreach (var evt in events)
        {
            Console.WriteLine($"{evt.event_version,-10} {evt.event_type,-30} {evt.created_at:yyyy-MM-dd HH:mm:ss.fff}");
        }
    }
}

async Task TimeTravelAsync(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: time-travel <timestamp> [--limit <n>]");
        Console.WriteLine("  timestamp format: yyyy-MM-dd HH:mm:ss");
        return;
    }

    var timestamp = DateTime.Parse(args[0]);
    var limit = 20;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--limit" && i + 1 < args.Length)
        {
            limit = int.Parse(args[++i]);
        }
    }

    await using var connection = new NpgsqlConnection(connectionString);

    Console.WriteLine($"=== Graph State at {timestamp:yyyy-MM-dd HH:mm:ss} ===\n");

    // Get active memories at timestamp
    var sql = @"
        WITH latest_states AS (
            SELECT DISTINCT ON (memory_id)
                memory_id,
                state_at,
                content,
                confidence,
                layer,
                is_active
            FROM memory_state_timeline
            WHERE state_at <= @Timestamp
            ORDER BY memory_id, state_at DESC
        )
        SELECT memory_id, state_at, content, confidence, layer
        FROM latest_states
        WHERE is_active = TRUE
        ORDER BY state_at DESC
        LIMIT @Limit";

    var memories = await connection.QueryAsync<dynamic>(sql, new { Timestamp = timestamp, Limit = limit });

    Console.WriteLine($"{"Memory ID",-38} {"Layer",-12} {"Confidence",-12} {"State At",-20}");
    Console.WriteLine(new string('-', 90));

    foreach (var m in memories)
    {
        Console.WriteLine($"{m.memory_id,-38} {m.layer,-12} {m.confidence:F3,-12} {m.state_at:MM-dd HH:mm:ss}");
    }

    var totalCount = await connection.ExecuteScalarAsync<int>(@"
        SELECT COUNT(DISTINCT memory_id)
        FROM memory_state_timeline
        WHERE state_at <= @Timestamp", new { Timestamp = timestamp });

    Console.WriteLine($"\nShowing {memories.Count()} of {totalCount} active memories at this time");
}

Task HelpAsync(string[] args)
{
    Console.WriteLine(@"
Replay Tool for Deterministic Memory Engine

Usage: dotnet script replay_tool.cs -- <command> [args]

Commands:
  list-sessions              List recent inference sessions
  show-session <id>          Show details of a specific session
  replay-session <id>        Replay an inference session
  verify-replay <orig> <rep> Verify replay determinism

  list-snapshots             List memory snapshots
  show-snapshot <id>         Show snapshot details
  compare-snapshots <a> <b>  Compare two snapshots

  memory-history <id>        Show history of a specific memory
  time-travel <timestamp>    View graph state at a specific time

  help                       Show this help message

Environment Variables:
  POSTGRES_CONNECTION        Database connection string

Examples:
  dotnet script replay_tool.cs -- list-sessions
  dotnet script replay_tool.cs -- show-session 550e8400-e29b-41d4-a716-446655440000
  dotnet script replay_tool.cs -- replay-session 550e8400-e29b-41d4-a716-446655440000 --seed 12345
  dotnet script replay_tool.cs -- time-travel ""2024-01-15 10:30:00"" --limit 50
");
    return Task.CompletedTask;
}

// Utility functions

string ComputeHash(string input)
{
    using var sha256 = System.Security.Cryptography.SHA256.Create();
    var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
    return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
