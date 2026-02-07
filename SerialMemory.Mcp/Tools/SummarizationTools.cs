using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Services;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// AI-powered summarization tools. Summarizes sessions and recent context
/// using an LLM, storing results as session_summary typed memories.
/// Gracefully degrades when no LLM is configured.
/// </summary>
public sealed class SummarizationTools(
    KnowledgeGraphService kgService,
    ILlmService llmService,
    ILogger logger)
{
    private const string SessionSummaryPrompt = """
        You are a session summarizer for a knowledge graph memory system.
        Summarize the following session memories into a concise, actionable summary.
        Focus on:
        1. Key decisions made and their rationale
        2. Problems encountered and how they were solved
        3. Patterns or heuristics discovered
        4. Files and components that were modified
        5. Next steps or open questions

        Format as structured markdown with clear sections.
        Keep it under 500 words. Be specific — include file names, function names, and error messages.
        """;

    private const string ContextSummaryPrompt = """
        You are a context summarizer for a knowledge graph memory system.
        Summarize the following recent memories into a concise context briefing.
        Focus on:
        1. What work has been done recently
        2. Current state of ongoing tasks
        3. Important decisions still in effect
        4. Known issues or blockers

        Format as structured markdown. Keep it under 300 words.
        """;

    /// <summary>
    /// MCP tool: summarize all memories from a session using LLM.
    /// </summary>
    public async Task<object> HandleSummarizeSession(JsonNode? arguments)
    {
        var sessionIdStr = arguments?["session_id"]?.GetValue<string>();
        var includeAutoCaptures = arguments?["include_auto_captures"]?.GetValue<bool>() ?? true;
        var maxMemories = Math.Clamp(arguments?["max_memories"]?.GetValue<int>() ?? 100, 1, 500);
        var storeSummary = arguments?["store_summary"]?.GetValue<bool>() ?? true;

        Guid? sessionId = Guid.TryParse(sessionIdStr, out var parsed) ? parsed : null;

        if (!sessionId.HasValue)
            return new { type = "text", text = "session_id is required. Provide the session GUID to summarize." };

        var memories = await kgService.GetMemoriesBySessionAsync(sessionId.Value, maxMemories);

        if (!includeAutoCaptures)
        {
            memories = memories.Where(m => m.MemoryType != "auto_capture").ToList();
        }

        if (memories.Count == 0)
            return new { type = "text", text = $"No memories found for session {sessionId}." };

        var content = BuildMemoryContent(memories);
        var summary = await llmService.ChatAsync(content, SessionSummaryPrompt, temperature: 0.3f, maxTokens: 1000);

        if (storeSummary)
        {
            await kgService.IngestMemoryAsync(
                content: summary,
                source: "summarization",
                sessionId: sessionId,
                metadata: new Dictionary<string, object>
                {
                    ["memory_type"] = "session_summary",
                    ["summarized_memory_count"] = memories.Count,
                    ["llm_provider"] = llmService.ProviderName,
                    ["llm_model"] = llmService.ModelName
                },
                extractEntities: true,
                memoryType: "session_summary");
        }

        return new
        {
            type = "text",
            text = $"## Session Summary\n\n" +
                   $"**Session:** {sessionId}\n" +
                   $"**Memories summarized:** {memories.Count}\n" +
                   $"**LLM:** {llmService.ProviderName}/{llmService.ModelName}\n" +
                   $"**Stored:** {storeSummary}\n\n" +
                   summary
        };
    }

    /// <summary>
    /// MCP tool: summarize recent context (for PreCompact or manual use).
    /// </summary>
    public async Task<object> HandleSummarizeContext(JsonNode? arguments)
    {
        var hoursBack = Math.Clamp(arguments?["hours_back"]?.GetValue<int>() ?? 4, 1, 72);
        var maxMemories = Math.Clamp(arguments?["max_memories"]?.GetValue<int>() ?? 50, 1, 200);
        var storeSummary = arguments?["store_summary"]?.GetValue<bool>() ?? true;

        var fromUtc = DateTime.UtcNow.AddHours(-hoursBack);
        var toUtc = DateTime.UtcNow;

        var memories = await kgService.GetMemoriesByDateRangeAsync(fromUtc, toUtc, maxMemories);

        if (memories.Count == 0)
            return new { type = "text", text = $"No memories found in the last {hoursBack} hours." };

        var content = BuildMemoryContent(memories);
        var summary = await llmService.ChatAsync(content, ContextSummaryPrompt, temperature: 0.3f, maxTokens: 600);

        if (storeSummary)
        {
            await kgService.IngestMemoryAsync(
                content: summary,
                source: "summarization",
                metadata: new Dictionary<string, object>
                {
                    ["memory_type"] = "session_summary",
                    ["hours_back"] = hoursBack,
                    ["summarized_memory_count"] = memories.Count,
                    ["llm_provider"] = llmService.ProviderName,
                    ["llm_model"] = llmService.ModelName
                },
                extractEntities: true,
                memoryType: "session_summary");
        }

        return new
        {
            type = "text",
            text = $"## Context Summary\n\n" +
                   $"**Period:** last {hoursBack} hours\n" +
                   $"**Memories summarized:** {memories.Count}\n" +
                   $"**LLM:** {llmService.ProviderName}/{llmService.ModelName}\n" +
                   $"**Stored:** {storeSummary}\n\n" +
                   summary
        };
    }

    /// <summary>
    /// Internal: called by HandleEndSession for automatic session summarization.
    /// </summary>
    public async Task SummarizeSessionInternalAsync(Guid sessionId)
    {
        var memories = await kgService.GetMemoriesBySessionAsync(sessionId, 100);

        if (memories.Count < 3)
        {
            logger.LogDebug("Skipping session summarization: only {Count} memories (minimum 3)", memories.Count);
            return;
        }

        var content = BuildMemoryContent(memories);
        var summary = await llmService.ChatAsync(content, SessionSummaryPrompt, temperature: 0.3f, maxTokens: 1000);

        await kgService.IngestMemoryAsync(
            content: summary,
            source: "auto-summarization",
            sessionId: sessionId,
            metadata: new Dictionary<string, object>
            {
                ["memory_type"] = "session_summary",
                ["trigger"] = "session_end",
                ["summarized_memory_count"] = memories.Count,
                ["llm_provider"] = llmService.ProviderName,
                ["llm_model"] = llmService.ModelName
            },
            extractEntities: true,
            memoryType: "session_summary");

        logger.LogInformation("Session {SessionId} summarized: {MemoryCount} memories -> session_summary",
            sessionId, memories.Count);
    }

    private static string BuildMemoryContent(List<Core.Models.Memory> memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Session Memories");
        sb.AppendLine();

        foreach (var memory in memories.OrderBy(m => m.CreatedAt))
        {
            sb.AppendLine($"## [{memory.CreatedAt:yyyy-MM-dd HH:mm}] ({memory.MemoryType ?? "knowledge"})");
            if (!string.IsNullOrEmpty(memory.Source))
                sb.AppendLine($"Source: {memory.Source}");
            sb.AppendLine();
            sb.AppendLine(memory.Content.Length > 500 ? memory.Content[..500] + "..." : memory.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
