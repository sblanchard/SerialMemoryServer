using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Reasoning;

/// <summary>
/// Service for orchestrating and persisting reasoning executions.
/// Creates execution records, calls MultiModelReasoningService, and persists results.
/// </summary>
public sealed class ReasoningRunService : IReasoningRunService
{
    private readonly IInternalDbConnectionFactory _connectionFactory;
    private readonly IMultiModelReasoningService _reasoningService;
    private readonly ILogger<ReasoningRunService> _logger;

    // Event for SignalR broadcasting (set by ReasoningHub)
    public event Func<Guid, ReasoningTrace, Task>? OnTraceAdded;
    public event Func<Guid, ReasoningExecution, Task>? OnExecutionCompleted;

    public ReasoningRunService(
        IInternalDbConnectionFactory connectionFactory,
        IMultiModelReasoningService reasoningService,
        ILogger<ReasoningRunService> logger)
    {
        _connectionFactory = connectionFactory;
        _reasoningService = reasoningService;
        _logger = logger;
    }

    public async Task<Guid> RunByMemoryAsync(
        Guid tenantId,
        string userId,
        Guid memoryId,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var execution = new ReasoningExecution
        {
            TenantId = tenantId,
            UserId = userId,
            InputType = ReasoningInputType.Memory,
            InputReference = memoryId.ToString()
        };

        return await ExecuteReasoningAsync(
            execution,
            () => _reasoningService.ReasonMemoryAsync(memoryId, maxDurationMs, cancellationToken),
            cancellationToken);
    }

    public async Task<Guid> RunByQueryAsync(
        Guid tenantId,
        string userId,
        string query,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var execution = new ReasoningExecution
        {
            TenantId = tenantId,
            UserId = userId,
            InputType = ReasoningInputType.Query,
            InputReference = query
        };

        // For query-based reasoning, we use the general ReasonAsync with no project filter
        return await ExecuteReasoningAsync(
            execution,
            () => _reasoningService.ReasonAsync(null, maxDurationMs, cancellationToken),
            cancellationToken);
    }

    public async Task<Guid> RunByProjectAsync(
        Guid tenantId,
        string userId,
        string? projectName,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var execution = new ReasoningExecution
        {
            TenantId = tenantId,
            UserId = userId,
            InputType = ReasoningInputType.Project,
            InputReference = projectName
        };

        return await ExecuteReasoningAsync(
            execution,
            () => _reasoningService.ReasonAsync(projectName, maxDurationMs, cancellationToken),
            cancellationToken);
    }

    private async Task<Guid> ExecuteReasoningAsync(
        ReasoningExecution execution,
        Func<Task<MultiModelReasoningResult>> reasoningFunc,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting reasoning execution {ExecutionId} for tenant {TenantId}, type {InputType}",
            execution.Id, execution.TenantId, execution.InputType);

        using var conn = await _connectionFactory.OpenInternalWithTenantAsync(execution.TenantId, cancellationToken);

        // Insert execution record as pending
        await InsertExecutionAsync(conn, execution);

        // Update to running
        execution.Status = ReasoningExecutionStatus.Running;
        execution.StartedAt = DateTimeOffset.UtcNow;
        await UpdateExecutionStatusAsync(conn, execution);

        try
        {
            // Run the reasoning
            var result = await reasoningFunc();

            // Update execution with results
            execution.Status = ReasoningExecutionStatus.Completed;
            execution.CompletedAt = DateTimeOffset.UtcNow;
            execution.ModelsUsed = result.ModelsUsed;
            execution.SuccessfulModels = result.SuccessfulModels;
            execution.TotalDurationMs = result.TotalDurationMs;
            execution.OverallConfidence = result.OverallConfidence;
            execution.InputHash = result.InputHash;
            execution.InsightCount = result.Insights.Count;
            execution.DisagreementCount = result.Disagreements.Count;

            // Persist all traces, insights, and disagreements
            var traces = await PersistTracesAsync(conn, execution.Id, execution.TenantId, result.Trace);
            await PersistInsightsAsync(conn, execution.Id, execution.TenantId, result.Insights);
            await PersistDisagreementsAsync(conn, execution.Id, execution.TenantId, result.Disagreements);

            // Update final status
            await UpdateExecutionStatusAsync(conn, execution);

            _logger.LogInformation("Reasoning execution {ExecutionId} completed successfully. Duration: {Duration}ms, Insights: {Insights}, Confidence: {Confidence:P0}",
                execution.Id, execution.TotalDurationMs, execution.InsightCount, execution.OverallConfidence);

            // Notify listeners
            if (OnExecutionCompleted != null)
            {
                execution.Traces.AddRange(traces);
                await OnExecutionCompleted(execution.Id, execution);
            }

            return execution.Id;
        }
        catch (OperationCanceledException)
        {
            execution.Status = ReasoningExecutionStatus.Cancelled;
            execution.CompletedAt = DateTimeOffset.UtcNow;
            execution.ErrorMessage = "Execution was cancelled";
            await UpdateExecutionStatusAsync(conn, execution);

            _logger.LogWarning("Reasoning execution {ExecutionId} was cancelled", execution.Id);
            throw;
        }
        catch (Exception ex)
        {
            execution.Status = ReasoningExecutionStatus.Failed;
            execution.CompletedAt = DateTimeOffset.UtcNow;
            execution.ErrorMessage = ex.Message;
            execution.ErrorDetails = new Dictionary<string, object>
            {
                ["exceptionType"] = ex.GetType().Name,
                ["stackTrace"] = ex.StackTrace ?? ""
            };
            await UpdateExecutionStatusAsync(conn, execution);

            _logger.LogError(ex, "Reasoning execution {ExecutionId} failed", execution.Id);
            throw;
        }
    }

    private static async Task InsertExecutionAsync(IDbConnection conn, ReasoningExecution execution)
    {
        const string sql = """
            INSERT INTO reasoning_executions (
                id, tenant_id, user_id, input_type, input_reference, input_hash,
                status, started_at, completed_at,
                models_used, successful_models, total_duration_ms, overall_confidence,
                insight_count, disagreement_count, error_message, error_details,
                created_at, updated_at
            ) VALUES (
                @Id, @TenantId, @UserId, @InputType, @InputReference, @InputHash,
                @Status::reasoning_status, @StartedAt, @CompletedAt,
                @ModelsUsed, @SuccessfulModels, @TotalDurationMs, @OverallConfidence,
                @InsightCount, @DisagreementCount, @ErrorMessage, @ErrorDetails::jsonb,
                @CreatedAt, @UpdatedAt
            )
            """;

        await conn.ExecuteAsync(sql, new
        {
            execution.Id,
            execution.TenantId,
            execution.UserId,
            InputType = execution.InputType.ToString().ToLowerInvariant(),
            execution.InputReference,
            execution.InputHash,
            Status = execution.Status.ToString().ToLowerInvariant(),
            execution.StartedAt,
            execution.CompletedAt,
            execution.ModelsUsed,
            execution.SuccessfulModels,
            execution.TotalDurationMs,
            execution.OverallConfidence,
            execution.InsightCount,
            execution.DisagreementCount,
            execution.ErrorMessage,
            ErrorDetails = execution.ErrorDetails != null
                ? JsonSerializer.Serialize(execution.ErrorDetails)
                : null,
            execution.CreatedAt,
            execution.UpdatedAt
        });
    }

    private static async Task UpdateExecutionStatusAsync(IDbConnection conn, ReasoningExecution execution)
    {
        const string sql = """
            UPDATE reasoning_executions SET
                status = @Status::reasoning_status,
                started_at = @StartedAt,
                completed_at = @CompletedAt,
                models_used = @ModelsUsed,
                successful_models = @SuccessfulModels,
                total_duration_ms = @TotalDurationMs,
                overall_confidence = @OverallConfidence,
                insight_count = @InsightCount,
                disagreement_count = @DisagreementCount,
                error_message = @ErrorMessage,
                error_details = @ErrorDetails::jsonb,
                input_hash = @InputHash,
                updated_at = NOW()
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, new
        {
            execution.Id,
            Status = execution.Status.ToString().ToLowerInvariant(),
            execution.StartedAt,
            execution.CompletedAt,
            execution.ModelsUsed,
            execution.SuccessfulModels,
            execution.TotalDurationMs,
            execution.OverallConfidence,
            execution.InsightCount,
            execution.DisagreementCount,
            execution.ErrorMessage,
            execution.InputHash,
            ErrorDetails = execution.ErrorDetails != null
                ? JsonSerializer.Serialize(execution.ErrorDetails)
                : null
        });
    }

    private async Task<List<ReasoningTrace>> PersistTracesAsync(
        IDbConnection conn,
        Guid executionId,
        Guid tenantId,
        List<ModelTrace> traces)
    {
        var reasoningTraces = new List<ReasoningTrace>();
        var stepOrder = 0;

        foreach (var trace in traces)
        {
            var reasoningTrace = new ReasoningTrace
            {
                ExecutionId = executionId,
                TenantId = tenantId,
                StepId = $"{trace.Role.ToString().ToLowerInvariant()}-{stepOrder + 1}",
                StepOrder = stepOrder++,
                Role = trace.Role,
                ModelName = trace.Model,
                ModelVersion = trace.Version,
                DurationMs = trace.DurationMs,
                Success = trace.Success,
                ErrorMessage = trace.Error,
                StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-trace.DurationMs),
                CompletedAt = DateTimeOffset.UtcNow
            };

            const string sql = """
                INSERT INTO reasoning_traces (
                    id, execution_id, tenant_id, step_id, parent_step_ids, step_order,
                    role, model_name, model_version,
                    started_at, completed_at, duration_ms, success,
                    input_context, output_raw, output_parsed,
                    error_message, error_code, created_at
                ) VALUES (
                    @Id, @ExecutionId, @TenantId, @StepId, @ParentStepIds, @StepOrder,
                    @Role::reasoning_role, @ModelName, @ModelVersion,
                    @StartedAt, @CompletedAt, @DurationMs, @Success,
                    @InputContext::jsonb, @OutputRaw, @OutputParsed::jsonb,
                    @ErrorMessage, @ErrorCode, @CreatedAt
                )
                """;

            await conn.ExecuteAsync(sql, new
            {
                reasoningTrace.Id,
                reasoningTrace.ExecutionId,
                reasoningTrace.TenantId,
                reasoningTrace.StepId,
                ParentStepIds = reasoningTrace.ParentStepIds.ToArray(),
                reasoningTrace.StepOrder,
                Role = reasoningTrace.Role.ToString().ToLowerInvariant(),
                reasoningTrace.ModelName,
                reasoningTrace.ModelVersion,
                reasoningTrace.StartedAt,
                reasoningTrace.CompletedAt,
                reasoningTrace.DurationMs,
                reasoningTrace.Success,
                InputContext = reasoningTrace.InputContext != null
                    ? JsonSerializer.Serialize(reasoningTrace.InputContext)
                    : null,
                reasoningTrace.OutputRaw,
                OutputParsed = reasoningTrace.OutputParsed != null
                    ? JsonSerializer.Serialize(reasoningTrace.OutputParsed)
                    : null,
                reasoningTrace.ErrorMessage,
                reasoningTrace.ErrorCode,
                reasoningTrace.CreatedAt
            });

            reasoningTraces.Add(reasoningTrace);

            // Notify listeners of new trace
            if (OnTraceAdded != null)
            {
                await OnTraceAdded(executionId, reasoningTrace);
            }
        }

        return reasoningTraces;
    }

    private static async Task PersistInsightsAsync(
        IDbConnection conn,
        Guid executionId,
        Guid tenantId,
        List<MergedInsight> insights)
    {
        foreach (var insight in insights)
        {
            var reasoningInsight = new ReasoningInsight
            {
                ExecutionId = executionId,
                TenantId = tenantId,
                InsightType = insight.Type.ToString().ToLowerInvariant(),
                Content = insight.Message,
                Confidence = insight.Confidence,
                SourceModels = insight.SourceModels,
                AgreementCount = insight.AgreementCount,
                RelatedEntityIds = insight.AffectedEntities ?? []
            };

            const string sql = """
                INSERT INTO reasoning_insights (
                    id, execution_id, tenant_id, insight_type, content, confidence,
                    source_models, agreement_count, related_entity_ids, related_memory_ids,
                    metadata, created_at
                ) VALUES (
                    @Id, @ExecutionId, @TenantId, @InsightType, @Content, @Confidence,
                    @SourceModels, @AgreementCount, @RelatedEntityIds, @RelatedMemoryIds,
                    @Metadata::jsonb, @CreatedAt
                )
                """;

            await conn.ExecuteAsync(sql, new
            {
                reasoningInsight.Id,
                reasoningInsight.ExecutionId,
                reasoningInsight.TenantId,
                reasoningInsight.InsightType,
                reasoningInsight.Content,
                reasoningInsight.Confidence,
                SourceModels = reasoningInsight.SourceModels.ToArray(),
                reasoningInsight.AgreementCount,
                RelatedEntityIds = reasoningInsight.RelatedEntityIds.ToArray(),
                RelatedMemoryIds = reasoningInsight.RelatedMemoryIds.ToArray(),
                Metadata = reasoningInsight.Metadata != null
                    ? JsonSerializer.Serialize(reasoningInsight.Metadata)
                    : null,
                reasoningInsight.CreatedAt
            });
        }
    }

    private static async Task PersistDisagreementsAsync(
        IDbConnection conn,
        Guid executionId,
        Guid tenantId,
        List<ModelDisagreement> disagreements)
    {
        foreach (var disagreement in disagreements)
        {
            var severity = disagreement.DisagreementScore switch
            {
                > 0.7f => "high",
                > 0.4f => "medium",
                _ => "low"
            };

            // Build topic from unique insights
            var topic = string.Join("; ", disagreement.PrimaryOnlyInsights.Take(2)
                .Concat(disagreement.SecondaryOnlyInsights.Take(2)));

            var reasoningDisagreement = new ReasoningDisagreement
            {
                ExecutionId = executionId,
                TenantId = tenantId,
                Topic = string.IsNullOrEmpty(topic) ? "General disagreement" : topic,
                ModelA = disagreement.PrimaryModel,
                PositionA = string.Join("; ", disagreement.PrimaryOnlyInsights.Take(3)),
                ModelB = disagreement.SecondaryModel,
                PositionB = string.Join("; ", disagreement.SecondaryOnlyInsights.Take(3)),
                Severity = severity
            };

            const string sql = """
                INSERT INTO reasoning_disagreements (
                    id, execution_id, tenant_id, topic, model_a, position_a,
                    model_b, position_b, severity, resolved, resolution,
                    resolved_at, resolved_by, created_at
                ) VALUES (
                    @Id, @ExecutionId, @TenantId, @Topic, @ModelA, @PositionA,
                    @ModelB, @PositionB, @Severity, @Resolved, @Resolution,
                    @ResolvedAt, @ResolvedBy, @CreatedAt
                )
                """;

            await conn.ExecuteAsync(sql, new
            {
                reasoningDisagreement.Id,
                reasoningDisagreement.ExecutionId,
                reasoningDisagreement.TenantId,
                reasoningDisagreement.Topic,
                reasoningDisagreement.ModelA,
                reasoningDisagreement.PositionA,
                reasoningDisagreement.ModelB,
                reasoningDisagreement.PositionB,
                reasoningDisagreement.Severity,
                reasoningDisagreement.Resolved,
                reasoningDisagreement.Resolution,
                reasoningDisagreement.ResolvedAt,
                reasoningDisagreement.ResolvedBy,
                reasoningDisagreement.CreatedAt
            });
        }
    }

    public async Task<ReasoningExecution?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.OpenInternalAsync(cancellationToken);

        const string executionSql = """
            SELECT
                id, tenant_id, user_id, input_type, input_reference, input_hash,
                status, started_at, completed_at,
                models_used, successful_models, total_duration_ms, overall_confidence,
                insight_count, disagreement_count, error_message, error_details,
                created_at, updated_at
            FROM reasoning_executions
            WHERE id = @ExecutionId
            """;

        var executionRow = await conn.QuerySingleOrDefaultAsync<dynamic>(executionSql, new { ExecutionId = executionId });
        if (executionRow == null) return null;

        var execution = MapExecution(executionRow);

        // Load traces
        const string tracesSql = """
            SELECT
                id, execution_id, tenant_id, step_id, parent_step_ids, step_order,
                role, model_name, model_version,
                started_at, completed_at, duration_ms, success,
                input_context, output_raw, output_parsed,
                error_message, error_code, created_at
            FROM reasoning_traces
            WHERE execution_id = @ExecutionId
            ORDER BY step_order
            """;

        var traceRows = await conn.QueryAsync<dynamic>(tracesSql, new { ExecutionId = executionId });
        foreach (var row in traceRows)
        {
            execution.Traces.Add(MapTrace(row));
        }

        // Load insights
        const string insightsSql = """
            SELECT
                id, execution_id, tenant_id, insight_type, content, confidence,
                source_models, agreement_count, related_entity_ids, related_memory_ids,
                metadata, created_at
            FROM reasoning_insights
            WHERE execution_id = @ExecutionId
            ORDER BY confidence DESC
            """;

        var insightRows = await conn.QueryAsync<dynamic>(insightsSql, new { ExecutionId = executionId });
        foreach (var row in insightRows)
        {
            execution.Insights.Add(MapInsight(row));
        }

        // Load disagreements
        const string disagreementsSql = """
            SELECT
                id, execution_id, tenant_id, topic, model_a, position_a,
                model_b, position_b, severity, resolved, resolution,
                resolved_at, resolved_by, created_at
            FROM reasoning_disagreements
            WHERE execution_id = @ExecutionId
            """;

        var disagreementRows = await conn.QueryAsync<dynamic>(disagreementsSql, new { ExecutionId = executionId });
        foreach (var row in disagreementRows)
        {
            execution.Disagreements.Add(MapDisagreement(row));
        }

        return execution;
    }

    public async Task<List<ReasoningExecutionSummary>> GetRecentExecutionsAsync(
        Guid tenantId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.OpenInternalWithTenantAsync(tenantId, cancellationToken);

        const string sql = """
            SELECT
                id, tenant_id, user_id, input_type, input_reference,
                status, started_at, completed_at,
                models_used, successful_models, total_duration_ms, overall_confidence,
                insight_count, disagreement_count, error_message, created_at
            FROM reasoning_executions
            WHERE tenant_id = @TenantId
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<dynamic>(sql, new { TenantId = tenantId, Limit = limit });
        return rows.Select(MapSummary).ToList();
    }

    public async Task<bool> CancelExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.OpenInternalAsync(cancellationToken);

        const string sql = """
            UPDATE reasoning_executions
            SET status = 'cancelled'::reasoning_status,
                completed_at = NOW(),
                error_message = 'Cancelled by user',
                updated_at = NOW()
            WHERE id = @ExecutionId
              AND status IN ('pending'::reasoning_status, 'running'::reasoning_status)
            RETURNING id
            """;

        var result = await conn.QuerySingleOrDefaultAsync<Guid?>(sql, new { ExecutionId = executionId });
        return result.HasValue;
    }

    #region Mapping Methods

    private static ReasoningExecution MapExecution(dynamic row)
    {
        return new ReasoningExecution
        {
            Id = row.id,
            TenantId = row.tenant_id,
            UserId = row.user_id,
            InputType = Enum.Parse<ReasoningInputType>(row.input_type, ignoreCase: true),
            InputReference = row.input_reference,
            InputHash = row.input_hash,
            Status = Enum.Parse<ReasoningExecutionStatus>(row.status, ignoreCase: true),
            StartedAt = row.started_at,
            CompletedAt = row.completed_at,
            ModelsUsed = row.models_used,
            SuccessfulModels = row.successful_models,
            TotalDurationMs = row.total_duration_ms,
            OverallConfidence = row.overall_confidence,
            InsightCount = row.insight_count,
            DisagreementCount = row.disagreement_count,
            ErrorMessage = row.error_message,
            ErrorDetails = row.error_details != null
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.error_details)
                : null,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at
        };
    }

    private static ReasoningTrace MapTrace(dynamic row)
    {
        return new ReasoningTrace
        {
            Id = row.id,
            ExecutionId = row.execution_id,
            TenantId = row.tenant_id,
            StepId = row.step_id,
            ParentStepIds = row.parent_step_ids != null
                ? ((string[])row.parent_step_ids).ToList()
                : [],
            StepOrder = row.step_order,
            Role = Enum.Parse<ReasoningRole>(row.role, ignoreCase: true),
            ModelName = row.model_name,
            ModelVersion = row.model_version,
            StartedAt = row.started_at,
            CompletedAt = row.completed_at,
            DurationMs = row.duration_ms,
            Success = row.success,
            InputContext = row.input_context != null
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.input_context)
                : null,
            OutputRaw = row.output_raw,
            OutputParsed = row.output_parsed != null
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.output_parsed)
                : null,
            ErrorMessage = row.error_message,
            ErrorCode = row.error_code,
            CreatedAt = row.created_at
        };
    }

    private static ReasoningInsight MapInsight(dynamic row)
    {
        return new ReasoningInsight
        {
            Id = row.id,
            ExecutionId = row.execution_id,
            TenantId = row.tenant_id,
            InsightType = row.insight_type,
            Content = row.content,
            Confidence = row.confidence,
            SourceModels = row.source_models != null
                ? ((string[])row.source_models).ToList()
                : [],
            AgreementCount = row.agreement_count,
            RelatedEntityIds = row.related_entity_ids != null
                ? ((Guid[])row.related_entity_ids).ToList()
                : [],
            RelatedMemoryIds = row.related_memory_ids != null
                ? ((Guid[])row.related_memory_ids).ToList()
                : [],
            Metadata = row.metadata != null
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata)
                : null,
            CreatedAt = row.created_at
        };
    }

    private static ReasoningDisagreement MapDisagreement(dynamic row)
    {
        return new ReasoningDisagreement
        {
            Id = row.id,
            ExecutionId = row.execution_id,
            TenantId = row.tenant_id,
            Topic = row.topic,
            ModelA = row.model_a,
            PositionA = row.position_a,
            ModelB = row.model_b,
            PositionB = row.position_b,
            Severity = row.severity,
            Resolved = row.resolved,
            Resolution = row.resolution,
            ResolvedAt = row.resolved_at,
            ResolvedBy = row.resolved_by,
            CreatedAt = row.created_at
        };
    }

    private static ReasoningExecutionSummary MapSummary(dynamic row)
    {
        return new ReasoningExecutionSummary
        {
            Id = row.id,
            TenantId = row.tenant_id,
            UserId = row.user_id,
            InputType = Enum.Parse<ReasoningInputType>(row.input_type, ignoreCase: true),
            InputReference = row.input_reference,
            Status = Enum.Parse<ReasoningExecutionStatus>(row.status, ignoreCase: true),
            StartedAt = row.started_at,
            CompletedAt = row.completed_at,
            ModelsUsed = row.models_used,
            SuccessfulModels = row.successful_models,
            TotalDurationMs = row.total_duration_ms,
            OverallConfidence = row.overall_confidence,
            InsightCount = row.insight_count,
            DisagreementCount = row.disagreement_count,
            ErrorMessage = row.error_message,
            CreatedAt = row.created_at
        };
    }

    #endregion
}
