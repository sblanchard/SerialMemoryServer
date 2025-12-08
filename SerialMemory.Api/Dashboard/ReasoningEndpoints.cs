using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Reasoning endpoints for multi-model reasoning execution and DAG visualization.
/// Persists executions and enables real-time tracking via SignalR.
/// </summary>
public static class ReasoningEndpoints
{
    public static void MapReasoningEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/reasoning")
            .WithTags("Reasoning")
            .RequireAuthorization();

        // =============================================================================
        // POST /reasoning/run/memory - Run reasoning on a specific memory
        // =============================================================================
        group.MapPost("/run/memory", async (
            [FromBody] RunReasoningByMemoryRequest request,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

            if (request.MemoryId == Guid.Empty)
                return Results.BadRequest(new { error = "validation_error", message = "MemoryId is required" });

            try
            {
                var executionId = await reasoningService.RunByMemoryAsync(
                    tenantId,
                    userId,
                    request.MemoryId,
                    request.MaxDurationMs ?? 30000,
                    ct);

                return Results.Accepted($"/reasoning/executions/{executionId}", new RunReasoningResponse
                {
                    ExecutionId = executionId,
                    Status = "running",
                    Message = "Reasoning execution started. Subscribe to SignalR for live updates."
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Reasoning Execution Failed");
            }
        })
        .WithName("RunReasoningByMemory")
        .WithDescription("Starts a multi-model reasoning execution for a specific memory")
        .Produces<RunReasoningResponse>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // POST /reasoning/run/query - Run reasoning on a text query
        // =============================================================================
        group.MapPost("/run/query", async (
            [FromBody] RunReasoningByQueryRequest request,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "validation_error", message = "Query is required" });

            try
            {
                var executionId = await reasoningService.RunByQueryAsync(
                    tenantId,
                    userId,
                    request.Query,
                    request.MaxDurationMs ?? 30000,
                    ct);

                return Results.Accepted($"/reasoning/executions/{executionId}", new RunReasoningResponse
                {
                    ExecutionId = executionId,
                    Status = "running",
                    Message = "Reasoning execution started. Subscribe to SignalR for live updates."
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Reasoning Execution Failed");
            }
        })
        .WithName("RunReasoningByQuery")
        .WithDescription("Starts a multi-model reasoning execution for a text query")
        .Produces<RunReasoningResponse>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // POST /reasoning/run/project - Run reasoning on a project
        // =============================================================================
        group.MapPost("/run/project", async (
            [FromBody] RunReasoningByProjectRequest request,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

            try
            {
                var executionId = await reasoningService.RunByProjectAsync(
                    tenantId,
                    userId,
                    request.ProjectName,
                    request.MaxDurationMs ?? 30000,
                    ct);

                return Results.Accepted($"/reasoning/executions/{executionId}", new RunReasoningResponse
                {
                    ExecutionId = executionId,
                    Status = "running",
                    Message = "Reasoning execution started. Subscribe to SignalR for live updates."
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Reasoning Execution Failed");
            }
        })
        .WithName("RunReasoningByProject")
        .WithDescription("Starts a multi-model reasoning execution for a project entity")
        .Produces<RunReasoningResponse>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // GET /reasoning/executions - List recent executions
        // =============================================================================
        group.MapGet("/executions", async (
            int? limit,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            var executions = await reasoningService.GetRecentExecutionsAsync(tenantId, limit ?? 20, ct);

            return Results.Ok(new ReasoningExecutionsResponse
            {
                Executions = executions.Select(e => new ReasoningExecutionSummaryDto
                {
                    Id = e.Id,
                    InputType = e.InputType.ToString().ToLowerInvariant(),
                    InputReference = e.InputReference,
                    Status = e.Status.ToString().ToLowerInvariant(),
                    StartedAt = e.StartedAt,
                    CompletedAt = e.CompletedAt,
                    ModelsUsed = e.ModelsUsed,
                    SuccessfulModels = e.SuccessfulModels,
                    TotalDurationMs = e.TotalDurationMs,
                    OverallConfidence = e.OverallConfidence,
                    InsightCount = e.InsightCount,
                    DisagreementCount = e.DisagreementCount,
                    ErrorMessage = e.ErrorMessage,
                    CreatedAt = e.CreatedAt
                }).ToList(),
                Count = executions.Count
            });
        })
        .WithName("ListReasoningExecutions")
        .WithDescription("Lists recent reasoning executions for the tenant")
        .Produces<ReasoningExecutionsResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /reasoning/executions/{id} - Get execution details
        // =============================================================================
        group.MapGet("/executions/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            var execution = await reasoningService.GetExecutionAsync(id, ct);

            if (execution == null)
                return Results.NotFound(new { error = "not_found", message = "Execution not found" });

            // Verify tenant ownership
            if (execution.TenantId != tenantId)
                return Results.NotFound(new { error = "not_found", message = "Execution not found" });

            return Results.Ok(new ReasoningExecutionDetailDto
            {
                Id = execution.Id,
                TenantId = execution.TenantId,
                UserId = execution.UserId,
                InputType = execution.InputType.ToString().ToLowerInvariant(),
                InputReference = execution.InputReference,
                InputHash = execution.InputHash,
                Status = execution.Status.ToString().ToLowerInvariant(),
                StartedAt = execution.StartedAt,
                CompletedAt = execution.CompletedAt,
                ModelsUsed = execution.ModelsUsed,
                SuccessfulModels = execution.SuccessfulModels,
                TotalDurationMs = execution.TotalDurationMs,
                OverallConfidence = execution.OverallConfidence,
                InsightCount = execution.InsightCount,
                DisagreementCount = execution.DisagreementCount,
                ErrorMessage = execution.ErrorMessage,
                CreatedAt = execution.CreatedAt,

                // DAG data
                Traces = execution.Traces.Select(t => new ReasoningTraceDto
                {
                    Id = t.Id,
                    StepId = t.StepId,
                    ParentStepIds = t.ParentStepIds,
                    StepOrder = t.StepOrder,
                    Role = t.Role.ToString().ToLowerInvariant(),
                    ModelName = t.ModelName,
                    ModelVersion = t.ModelVersion,
                    StartedAt = t.StartedAt,
                    CompletedAt = t.CompletedAt,
                    DurationMs = t.DurationMs,
                    Success = t.Success,
                    ErrorMessage = t.ErrorMessage
                }).ToList(),

                // Insights
                Insights = execution.Insights.Select(i => new ReasoningInsightDto
                {
                    Id = i.Id,
                    InsightType = i.InsightType,
                    Content = i.Content,
                    Confidence = i.Confidence,
                    SourceModels = i.SourceModels,
                    AgreementCount = i.AgreementCount,
                    RelatedEntityIds = i.RelatedEntityIds,
                    CreatedAt = i.CreatedAt
                }).ToList(),

                // Disagreements
                Disagreements = execution.Disagreements.Select(d => new ReasoningDisagreementDto
                {
                    Id = d.Id,
                    Topic = d.Topic,
                    ModelA = d.ModelA,
                    PositionA = d.PositionA,
                    ModelB = d.ModelB,
                    PositionB = d.PositionB,
                    Severity = d.Severity,
                    Resolved = d.Resolved,
                    Resolution = d.Resolution,
                    CreatedAt = d.CreatedAt
                }).ToList()
            });
        })
        .WithName("GetReasoningExecution")
        .WithDescription("Gets full details for a reasoning execution including DAG traces")
        .Produces<ReasoningExecutionDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /reasoning/executions/{id}/cancel - Cancel a running execution
        // =============================================================================
        group.MapPost("/executions/{id:guid}/cancel", async (
            Guid id,
            ClaimsPrincipal user,
            IReasoningRunService reasoningService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            // First verify the execution exists and belongs to this tenant
            var execution = await reasoningService.GetExecutionAsync(id, ct);
            if (execution == null || execution.TenantId != tenantId)
                return Results.NotFound(new { error = "not_found", message = "Execution not found" });

            var cancelled = await reasoningService.CancelExecutionAsync(id, ct);

            if (!cancelled)
                return Results.BadRequest(new { error = "cannot_cancel", message = "Execution is not in a cancellable state" });

            return Results.Ok(new { message = "Execution cancelled successfully", executionId = id });
        })
        .WithName("CancelReasoningExecution")
        .WithDescription("Cancels a running reasoning execution")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
    }

    private static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
        {
            return (Guid.Parse("00000000-0000-0000-0000-000000000000"), "self-hosted", "default");
        }

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
            throw new UnauthorizedAccessException("Invalid tenant_id claim");

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? throw new UnauthorizedAccessException("Missing user identifier");

        var workspaceId = user.FindFirst("workspace_id")?.Value ?? "default";

        return (tenantId, userId, workspaceId);
    }
}

// =============================================================================
// Request DTOs
// =============================================================================

public sealed record RunReasoningByMemoryRequest
{
    public Guid MemoryId { get; init; }
    public int? MaxDurationMs { get; init; }
}

public sealed record RunReasoningByQueryRequest
{
    public string Query { get; init; } = "";
    public int? MaxDurationMs { get; init; }
}

public sealed record RunReasoningByProjectRequest
{
    public string? ProjectName { get; init; }
    public int? MaxDurationMs { get; init; }
}

// =============================================================================
// Response DTOs
// =============================================================================

public sealed class RunReasoningResponse
{
    public Guid ExecutionId { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class ReasoningExecutionsResponse
{
    public List<ReasoningExecutionSummaryDto> Executions { get; init; } = [];
    public int Count { get; init; }
}

public sealed class ReasoningExecutionSummaryDto
{
    public Guid Id { get; init; }
    public string InputType { get; init; } = "";
    public string? InputReference { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public int TotalDurationMs { get; init; }
    public float OverallConfidence { get; init; }
    public int InsightCount { get; init; }
    public int DisagreementCount { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ReasoningExecutionDetailDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string UserId { get; init; } = "";
    public string InputType { get; init; } = "";
    public string? InputReference { get; init; }
    public string? InputHash { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public int TotalDurationMs { get; init; }
    public float OverallConfidence { get; init; }
    public int InsightCount { get; init; }
    public int DisagreementCount { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    // DAG nodes
    public List<ReasoningTraceDto> Traces { get; init; } = [];

    // Results
    public List<ReasoningInsightDto> Insights { get; init; } = [];
    public List<ReasoningDisagreementDto> Disagreements { get; init; } = [];
}

public sealed class ReasoningTraceDto
{
    public Guid Id { get; init; }
    public string StepId { get; init; } = "";
    public List<string> ParentStepIds { get; init; } = [];
    public int StepOrder { get; init; }
    public string Role { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string? ModelVersion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ReasoningInsightDto
{
    public Guid Id { get; init; }
    public string InsightType { get; init; } = "";
    public string Content { get; init; } = "";
    public float Confidence { get; init; }
    public List<string> SourceModels { get; init; } = [];
    public int AgreementCount { get; init; }
    public List<Guid> RelatedEntityIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ReasoningDisagreementDto
{
    public Guid Id { get; init; }
    public string Topic { get; init; } = "";
    public string ModelA { get; init; } = "";
    public string PositionA { get; init; } = "";
    public string ModelB { get; init; } = "";
    public string PositionB { get; init; } = "";
    public string? Severity { get; init; }
    public bool Resolved { get; init; }
    public string? Resolution { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
