using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using SerialMemory.ML;

namespace SerialMemory.Api.LLM;

/// <summary>
/// LLM API endpoints for chat completions and embeddings.
/// Uses OpenAI if configured, falls back to Ollama.
/// </summary>
public static class LlmEndpoints
{
    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/llm")
            .WithTags("LLM");

        // POST /api/llm/chat - Chat completion
        group.MapPost("/chat", async (
            ChatRequest request,
            ILlmService llmService,
            IUsageService usageService) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await llmService.ChatAsync(
                    request.Message,
                    request.SystemPrompt,
                    request.Temperature ?? 0.7f,
                    request.MaxTokens);

                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.LlmChat,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: true);

                return Results.Ok(new ChatResponse
                {
                    Content = response,
                    Model = llmService.ModelName,
                    Provider = llmService.ProviderName,
                    LatencyMs = (int)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.LlmChat,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: false,
                    errorMessage: ex.Message);

                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Chat completion failed");
            }
        });

        // POST /api/llm/embed - Generate embedding
        group.MapPost("/embed", async (
            EmbedRequest request,
            IEmbeddingService embeddingService,
            IUsageService usageService) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var embedding = await embeddingService.EmbedTextAsync(request.Text);

                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.Embedding,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: true);

                return Results.Ok(new EmbedResponse
                {
                    Embedding = embedding,
                    Dimension = embedding.Length,
                    LatencyMs = (int)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.Embedding,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: false,
                    errorMessage: ex.Message);

                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Embedding generation failed");
            }
        });

        // POST /api/llm/embed/batch - Batch embeddings
        group.MapPost("/embed/batch", async (
            BatchEmbedRequest request,
            IEmbeddingService embeddingService,
            IUsageService usageService) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var embeddings = await embeddingService.EmbedBatchAsync(request.Texts);

                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.Embedding,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: true,
                    metadata: new Dictionary<string, object> { ["count"] = request.Texts.Count });

                return Results.Ok(new BatchEmbedResponse
                {
                    Embeddings = embeddings,
                    Count = embeddings.Count,
                    Dimension = embeddings.FirstOrDefault()?.Length ?? 0,
                    LatencyMs = (int)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.Embedding,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: false,
                    errorMessage: ex.Message);

                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Batch embedding generation failed");
            }
        });

        // POST /api/llm/extract - Entity extraction
        group.MapPost("/extract", async (
            ExtractRequest request,
            IEntityExtractionService extractionService,
            IUsageService usageService) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (entities, relationships) = await extractionService.ExtractAllAsync(request.Text);

                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.EntityExtraction,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: true);

                return Results.Ok(new ExtractResponse
                {
                    Entities = entities,
                    Relationships = relationships,
                    LatencyMs = (int)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                usageService.TrackUsage(
                    UsageEventType.EntityExtraction,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    success: false,
                    errorMessage: ex.Message);

                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Entity extraction failed");
            }
        });

        // GET /api/llm/test - Test LLM connectivity
        group.MapGet("/test", async (
            ILlmService llmService,
            IEmbeddingService embeddingService,
            IEntityExtractionService extractionService) =>
        {
            var results = new Dictionary<string, object>();

            // Test chat
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var chatResponse = await llmService.ChatAsync("Say 'Hello' in exactly one word.", maxTokens: 10);
                sw.Stop();
                results["chat"] = new
                {
                    status = "ok",
                    provider = llmService.ProviderName,
                    model = llmService.ModelName,
                    response = chatResponse,
                    latencyMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                results["chat"] = new { status = "error", error = ex.Message };
            }

            // Test embedding
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var embedding = await embeddingService.EmbedTextAsync("test");
                sw.Stop();
                results["embedding"] = new
                {
                    status = "ok",
                    dimension = embedding.Length,
                    latencyMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                results["embedding"] = new { status = "error", error = ex.Message };
            }

            // Test entity extraction
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (entities, _) = await extractionService.ExtractAllAsync("John works at Microsoft on the Azure team.");
                sw.Stop();
                results["extraction"] = new
                {
                    status = "ok",
                    entitiesFound = entities.Count,
                    latencyMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                results["extraction"] = new { status = "error", error = ex.Message };
            }

            return Results.Ok(results);
        });

        // GET /api/llm/config - Current LLM configuration
        group.MapGet("/config", (ILlmService llmService, IEmbeddingService embeddingService) =>
        {
            return Results.Ok(new
            {
                chat = new
                {
                    provider = llmService.ProviderName,
                    model = llmService.ModelName
                },
                embedding = new
                {
                    provider = embeddingService.GetType().Name,
                    dimension = embeddingService.EmbeddingDimension
                }
            });
        });

        return app;
    }
}

// Request/Response DTOs

public sealed record ChatRequest
{
    public required string Message { get; init; }
    public string? SystemPrompt { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
}

public sealed record ChatResponse
{
    public required string Content { get; init; }
    public required string Model { get; init; }
    public required string Provider { get; init; }
    public required int LatencyMs { get; init; }
}

public sealed record EmbedRequest
{
    public required string Text { get; init; }
}

public sealed record EmbedResponse
{
    public required float[] Embedding { get; init; }
    public required int Dimension { get; init; }
    public required int LatencyMs { get; init; }
}

public sealed record BatchEmbedRequest
{
    public required List<string> Texts { get; init; }
}

public sealed record BatchEmbedResponse
{
    public required List<float[]> Embeddings { get; init; }
    public required int Count { get; init; }
    public required int Dimension { get; init; }
    public required int LatencyMs { get; init; }
}

public sealed record ExtractRequest
{
    public required string Text { get; init; }
}

public sealed record ExtractResponse
{
    public required List<ExtractedEntity> Entities { get; init; }
    public required List<ExtractedRelationship> Relationships { get; init; }
    public required int LatencyMs { get; init; }
}
