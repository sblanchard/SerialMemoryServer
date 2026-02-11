using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handlers for user profile tools: about_user, set_persona.
/// </summary>
internal sealed class UserProfileToolHandlers(
    KnowledgeGraphService kgService,
    ILogger logger)
{
    public async Task<object> HandleMemoryAboutUser(JsonNode? arguments)
    {
        var user = arguments?["user_id"]?.GetValue<string>() ?? "default_user";

        var persona = await kgService.GetUserPersonaAsync(user);

        if (persona.Count == 0)
        {
            return CreateTextResponse($"No persona information found for user: {user}");
        }

        var text = $"User Persona for {user}:\n\n";
        foreach (var (attrType, attributes) in persona)
        {
            text += $"**{char.ToUpper(attrType[0]) + attrType[1..]}:**\n";
            foreach (var (key, valueData) in attributes)
            {
                if (valueData is Dictionary<string, object> vd)
                {
                    text += $"  - {key}: {vd.GetValueOrDefault("value", "N/A")} (confidence: {vd.GetValueOrDefault("confidence", 1.0):F2})\n";
                }
            }
            text += "\n";
        }

        return CreateTextResponse(text);
    }

    public async Task<object> HandleSetUserPersona(JsonNode? arguments)
    {
        var attrType = arguments?["attribute_type"]?.GetValue<string>()?.Trim();
        var attrKey = arguments?["attribute_key"]?.GetValue<string>()?.Trim();
        var attrValue = arguments?["attribute_value"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrEmpty(attrType))
            throw new ArgumentException("attribute_type is required");
        if (string.IsNullOrEmpty(attrKey))
            throw new ArgumentException("attribute_key is required");
        if (string.IsNullOrEmpty(attrValue))
            throw new ArgumentException("attribute_value is required");

        var validTypes = new[] { "preference", "skill", "goal", "background" };
        if (!validTypes.Contains(attrType.ToLowerInvariant()))
            throw new ArgumentException($"attribute_type must be one of: {string.Join(", ", validTypes)}");

        var confidence = Math.Clamp(arguments?["confidence"]?.GetValue<float>() ?? 1.0f, 0f, 1f);
        var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

        try
        {
            await kgService.SetUserPersonaAttributeAsync(attrType, attrKey, attrValue, confidence, user);
            return CreateTextResponse($"User persona attribute set: {attrType}/{attrKey} = {attrValue} (confidence: {confidence:F2})");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("set_user_persona skipped: {Message}", ex.MessageText);
            return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable this feature.");
        }
    }
}
