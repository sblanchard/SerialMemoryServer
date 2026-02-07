using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Services;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handles auto-capture drain operations. Reads JSONL session logs
/// written by session-capture.sh hooks and ingests them as memories.
/// </summary>
public sealed class AutoCaptureTools(
    KnowledgeGraphService kgService,
    ILogger logger)
{
    private static readonly string SessionLogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-serialmemory", "sessions");

    private static readonly HashSet<string> ValidMemoryTypes =
        ["error", "decision", "pattern", "learning", "knowledge", "session_summary", "auto_capture"];

    /// <summary>
    /// MCP tool: drain captured JSONL entries into memories.
    /// </summary>
    public async Task<object> HandleDrainSessionCaptures(JsonNode? arguments)
    {
        var sessionIdStr = arguments?["session_id"]?.GetValue<string>()?.Trim();
        var maxEntries = Math.Clamp(arguments?["max_entries"]?.GetValue<int>() ?? 500, 1, 5000);
        var dryRun = arguments?["dry_run"]?.GetValue<bool>() ?? false;

        // Find the session log file
        var logFile = FindSessionLogFile(sessionIdStr);
        if (logFile == null)
        {
            return CreateTextResponse("No session capture file found. Ensure session-capture.sh hook is active.");
        }

        var entries = await ReadJsonlEntriesAsync(logFile, maxEntries);
        if (entries.Count == 0)
        {
            return CreateTextResponse("Session capture file is empty — nothing to drain.");
        }

        if (dryRun)
        {
            var preview = FormatDrainPreview(entries);
            return CreateTextResponse(
                $"## Dry Run — {entries.Count} entries found\n\n{preview}\n\n" +
                $"Set `dry_run: false` to ingest these as memories.");
        }

        var result = await DrainEntriesAsync(entries, sessionId: null);

        // Rename to .drained to prevent re-ingestion
        var drainedPath = logFile + ".drained";
        try
        {
            File.Move(logFile, drainedPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not rename {File} to .drained", logFile);
        }

        return CreateTextResponse(
            $"## Auto-Capture Drain Complete\n\n" +
            $"Entries processed: {result.EntriesProcessed}\n" +
            $"Memories created: {result.MemoriesCreated}\n" +
            $"Errors: {result.ErrorCount}\n" +
            $"Source file: {Path.GetFileName(logFile)} → .drained");
    }

    /// <summary>
    /// MCP tool: check capture buffer status without draining.
    /// </summary>
    public async Task<object> HandleCaptureStatus(JsonNode? arguments)
    {
        if (!Directory.Exists(SessionLogDir))
        {
            return CreateTextResponse("No capture directory found. Session capture hooks may not be configured.");
        }

        var files = Directory.GetFiles(SessionLogDir, "*.jsonl")
            .Where(f => !f.EndsWith(".drained"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (files.Count == 0)
        {
            return CreateTextResponse("No active session capture files found.");
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine("## Capture Status\n");

        var totalEntries = 0;
        foreach (var file in files.Take(5))
        {
            var entries = await ReadJsonlEntriesAsync(file, int.MaxValue);
            totalEntries += entries.Count;

            var fileName = Path.GetFileName(file);
            var firstTs = entries.FirstOrDefault()?.Timestamp;
            var lastTs = entries.LastOrDefault()?.Timestamp;
            var errorCount = entries.Count(e =>
                e.Tool?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
                e.Result?.Contains("error", StringComparison.OrdinalIgnoreCase) == true);

            text.AppendLine($"**{fileName}** — {entries.Count} entries");
            if (firstTs != null && lastTs != null)
            {
                text.AppendLine($"  Time span: {firstTs} → {lastTs}");
            }
            if (errorCount > 0)
            {
                text.AppendLine($"  Errors detected: {errorCount}");
            }
            text.AppendLine();
        }

        text.AppendLine($"**Total entries across {files.Count} file(s):** {totalEntries}");
        text.AppendLine("\nUse `drain_session_captures` to ingest into memory.");

        return CreateTextResponse(text.ToString());
    }

    /// <summary>
    /// Internal: called by HandleEndSession to auto-drain without JSON parsing.
    /// </summary>
    public async Task DrainInternalAsync(Guid sessionId)
    {
        var logFile = FindSessionLogFile(null);
        if (logFile == null)
        {
            logger.LogDebug("No session capture file to drain at session end");
            return;
        }

        var entries = await ReadJsonlEntriesAsync(logFile, 500);
        if (entries.Count == 0)
        {
            logger.LogDebug("Session capture file is empty — nothing to drain");
            return;
        }

        var result = await DrainEntriesAsync(entries, sessionId);

        logger.LogInformation(
            "Auto-capture drain: {Processed} entries → {Created} memories ({Errors} errors)",
            result.EntriesProcessed, result.MemoriesCreated, result.ErrorCount);

        // Rename to .drained
        try
        {
            File.Move(logFile, logFile + ".drained", overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not rename {File} to .drained", logFile);
        }
    }

    private string? FindSessionLogFile(string? sessionIdHint)
    {
        if (!Directory.Exists(SessionLogDir)) return null;

        // Try specific session ID first
        if (!string.IsNullOrEmpty(sessionIdHint))
        {
            var specific = Path.Combine(SessionLogDir, $"{sessionIdHint}.jsonl");
            if (File.Exists(specific)) return specific;
        }

        // Fall back to most recent active .jsonl file
        return Directory.GetFiles(SessionLogDir, "*.jsonl")
            .Where(f => !f.EndsWith(".drained"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static async Task<List<CaptureEntry>> ReadJsonlEntriesAsync(string filePath, int maxEntries)
    {
        var entries = new List<CaptureEntry>();

        await foreach (var line in File.ReadLinesAsync(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (entries.Count >= maxEntries) break;

            try
            {
                var node = JsonNode.Parse(line);
                if (node == null) continue;

                entries.Add(new CaptureEntry
                {
                    Timestamp = node["ts"]?.GetValue<string>(),
                    Tool = node["tool"]?.GetValue<string>(),
                    File = node["file"]?.GetValue<string>(),
                    Result = node["result"]?.GetValue<string>()
                });
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
        }

        return entries;
    }

    private async Task<DrainResult> DrainEntriesAsync(List<CaptureEntry> entries, Guid? sessionId)
    {
        var result = new DrainResult();

        // Group consecutive entries by tool type into batches
        var batches = GroupIntoBatches(entries);

        foreach (var batch in batches)
        {
            try
            {
                var content = FormatBatchContent(batch);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var metadata = new Dictionary<string, object>
                {
                    ["memory_type"] = "auto_capture",
                    ["trigger"] = "session_end",
                    ["entry_count"] = batch.Entries.Count,
                    ["tool_type"] = batch.ToolType
                };

                if (batch.FirstTimestamp != null)
                    metadata["first_ts"] = batch.FirstTimestamp;
                if (batch.LastTimestamp != null)
                    metadata["last_ts"] = batch.LastTimestamp;

                await kgService.IngestMemoryAsync(
                    content,
                    source: "auto-capture",
                    sessionId: sessionId,
                    metadata: metadata,
                    extractEntities: true,
                    dedupMode: "off");

                result.MemoriesCreated++;
                result.EntriesProcessed += batch.Entries.Count;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to ingest auto-capture batch: {Tool}", batch.ToolType);
                result.ErrorCount++;
                result.EntriesProcessed += batch.Entries.Count;
            }
        }

        return result;
    }

    private static List<EntryBatch> GroupIntoBatches(List<CaptureEntry> entries)
    {
        var batches = new List<EntryBatch>();
        EntryBatch? current = null;

        foreach (var entry in entries)
        {
            var toolType = NormalizeToolType(entry.Tool);

            if (current == null || current.ToolType != toolType)
            {
                current = new EntryBatch { ToolType = toolType };
                batches.Add(current);
            }

            current.Entries.Add(entry);
            current.FirstTimestamp ??= entry.Timestamp;
            current.LastTimestamp = entry.Timestamp;
        }

        return batches;
    }

    private static string NormalizeToolType(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return "unknown";

        var lower = tool.ToLowerInvariant();
        if (lower.Contains("write") || lower.Contains("edit")) return "file_edit";
        if (lower.Contains("bash")) return "bash_command";
        if (lower.Contains("read") || lower.Contains("glob") || lower.Contains("grep")) return "file_read";
        return lower;
    }

    private static string FormatBatchContent(EntryBatch batch)
    {
        var files = batch.Entries
            .Where(e => !string.IsNullOrEmpty(e.File))
            .Select(e => e.File!)
            .Distinct()
            .ToList();

        return batch.ToolType switch
        {
            "file_edit" => files.Count > 0
                ? $"Files modified: {string.Join(", ", files)}"
                : $"File edit operations ({batch.Entries.Count} actions)",

            "bash_command" => $"Commands executed ({batch.Entries.Count} actions)" +
                (files.Count > 0 ? $" — files: {string.Join(", ", files.Take(5))}" : ""),

            "file_read" => files.Count > 0
                ? $"Files read: {string.Join(", ", files.Take(10))}"
                : $"File read operations ({batch.Entries.Count} actions)",

            _ => $"Activity: {batch.ToolType} ({batch.Entries.Count} actions)" +
                (files.Count > 0 ? $" — {string.Join(", ", files.Take(5))}" : "")
        };
    }

    private static string FormatDrainPreview(List<CaptureEntry> entries)
    {
        var batches = GroupIntoBatches(entries);
        var lines = batches.Select(b =>
            $"- **{b.ToolType}**: {b.Entries.Count} entries" +
            (b.FirstTimestamp != null ? $" ({b.FirstTimestamp} → {b.LastTimestamp})" : ""));

        return string.Join("\n", lines);
    }

    private static object CreateTextResponse(string text) => new
    {
        content = new[] { new { type = "text", text } }
    };

    private sealed class CaptureEntry
    {
        public string? Timestamp { get; init; }
        public string? Tool { get; init; }
        public string? File { get; init; }
        public string? Result { get; init; }
    }

    private sealed class EntryBatch
    {
        public required string ToolType { get; init; }
        public List<CaptureEntry> Entries { get; } = [];
        public string? FirstTimestamp { get; set; }
        public string? LastTimestamp { get; set; }
    }

    private sealed class DrainResult
    {
        public int EntriesProcessed { get; set; }
        public int MemoriesCreated { get; set; }
        public int ErrorCount { get; set; }
    }
}
