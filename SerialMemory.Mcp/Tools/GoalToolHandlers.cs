using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handlers for goal tools: set, list, complete.
/// </summary>
internal sealed class GoalToolHandlers(
    KnowledgeGraphService kgService,
    ILogger logger)
{
    public async Task<object> HandleGoalSet(JsonNode? arguments)
    {
        var key = arguments?["key"]?.GetValue<string>()?.Trim();
        var description = arguments?["description"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key is required");
        if (string.IsNullOrEmpty(description))
            throw new ArgumentException("description is required");

        var priority = Math.Clamp(arguments?["priority"]?.GetValue<float>() ?? 1.0f, 0.1f, 1f);
        var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

        try
        {
            await kgService.SetGoalAsync(key, description, priority, user);
            return CreateTextResponse($"Goal set: **{key}** (priority: {priority:F1})\n{description}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("goal_set skipped: {Message}", ex.MessageText);
            return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
        }
    }

    public async Task<object> HandleGoalList(JsonNode? arguments)
    {
        var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

        try
        {
            var goals = await kgService.GetActiveGoalsAsync(user);

            if (goals.Count == 0)
                return CreateTextResponse("No active goals. Use `goal_set` to create one.");

            var text = $"## Active Goals ({goals.Count})\n\n";
            foreach (var goal in goals)
            {
                var priorityLabel = goal.Confidence >= 0.8f ? "HIGH" : goal.Confidence >= 0.5f ? "MEDIUM" : "LOW";
                text += $"- **{goal.AttributeKey}** [{priorityLabel}] — {goal.AttributeValue}\n";
                text += $"  *Updated: {goal.UpdatedAt:yyyy-MM-dd HH:mm}*\n";
            }
            return CreateTextResponse(text);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("goal_list skipped: {Message}", ex.MessageText);
            return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
        }
    }

    public async Task<object> HandleGoalComplete(JsonNode? arguments)
    {
        var key = arguments?["key"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key is required");

        var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

        try
        {
            await kgService.CompleteGoalAsync(key, user);
            return CreateTextResponse($"Goal completed: **{key}**");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("goal_complete skipped: {Message}", ex.MessageText);
            return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
        }
    }
}
