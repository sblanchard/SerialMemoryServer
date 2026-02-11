using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handlers for session lifecycle tools: init, end.
/// </summary>
internal sealed class SessionToolHandlers(
    KnowledgeGraphService kgService,
    McpSessionState sessionState,
    AutoCaptureTools autoCaptureTools,
    SummarizationTools? summarizationTools,
    ILogger logger)
{
    public async Task<object> HandleInitialiseSession(JsonNode? arguments)
    {
        var sessionName = arguments?["session_name"]?.GetValue<string>();
        var clientType = arguments?["client_type"]?.GetValue<string>();

        Dictionary<string, object>? metadata = null;
        if (arguments?["metadata"] is { } metadataNode)
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
        }

        var newSessionId = await kgService.CreateConversationSessionAsync(sessionName, clientType, metadata);
        sessionState.SetSession(newSessionId);

        return CreateTextResponse($"Conversation session initialized: {newSessionId}");
    }

    public async Task<object> HandleEndSession()
    {
        if (!sessionState.CurrentSessionId.HasValue)
            return CreateTextResponse("No active conversation session to end.");

        try
        {
            var sessionId = sessionState.CurrentSessionId.Value;

            // Phase 1: Auto-drain captured session activity
            try
            {
                await autoCaptureTools.DrainInternalAsync(sessionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-capture drain failed (non-fatal)");
            }

            // Phase 3: AI summarization (if LLM available)
            try
            {
                if (summarizationTools != null)
                {
                    await summarizationTools.SummarizeSessionInternalAsync(sessionId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Session summarization failed (non-fatal)");
            }

            // End the session
            await kgService.EndConversationSessionAsync(sessionId);
            sessionState.ClearSession();
            return CreateTextResponse($"Conversation session ended: {sessionId}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("end_conversation_session skipped: {Message}", ex.MessageText);
            sessionState.ClearSession();
            return CreateErrorResponse("Session tracking schema not available. Session cleared locally.");
        }
    }
}
