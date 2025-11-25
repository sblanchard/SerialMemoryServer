using System.Security.Cryptography;
using System.Text;
using SerialMemory.EventSourcing.Events;

namespace SerialMemory.EventSourcing.Aggregates;

/// <summary>
/// Memory aggregate root. Applies events to build current state.
/// State is rebuilt from events, never mutated directly.
/// </summary>
public sealed class MemoryAggregate
{
    private readonly List<IMemoryEvent> _uncommittedEvents = [];

    public Guid Id { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public float[] Embedding { get; private set; } = [];
    public MemoryLayer Layer { get; private set; } = MemoryLayer.L0_RAW;

    // Confidence & Decay
    public float ConfidenceScore { get; private set; } = 1.0f;
    public int HalfLifeDays { get; private set; } = 30;
    public DateTimeOffset LastReinforcedAt { get; private set; }

    // Integrity
    public Guid[] CausalParents { get; private set; } = [];
    public Guid[] ValidatedBy { get; private set; } = [];

    // Metadata
    public string? Source { get; private set; }
    public Guid? SessionId { get; private set; }
    public string UserId { get; private set; } = "default_user";
    public string[] Tags { get; private set; } = [];

    // State
    public bool IsActive { get; private set; } = true;
    public bool IsArchived { get; private set; }
    public bool IsExpired { get; private set; }
    public bool IsSplit { get; private set; }
    public Guid? MergedIntoId { get; private set; }
    public Guid[] ContradictionIds { get; private set; } = [];
    public Guid[] ChildMemoryIds { get; private set; } = [];

    // Access tracking
    public int RecallCount { get; private set; }
    public int IgnoreCount { get; private set; }
    public DateTimeOffset? LastRecalledAt { get; private set; }

    // Version tracking
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<IMemoryEvent> UncommittedEvents => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    /// <summary>
    /// Calculate current confidence with exponential decay.
    /// </summary>
    public float CurrentConfidence
    {
        get
        {
            var daysElapsed = (DateTimeOffset.UtcNow - LastReinforcedAt).TotalDays;
            var decayFactor = Math.Pow(0.5, daysElapsed / HalfLifeDays);
            return (float)Math.Max(0.0, ConfidenceScore * decayFactor);
        }
    }

    // ========================================================================
    // COMMAND METHODS (raise events)
    // ========================================================================

    public static MemoryAggregate Create(
        string content,
        float[] embedding,
        MemoryLayer layer = MemoryLayer.L0_RAW,
        float confidenceScore = 1.0f,
        int halfLifeDays = 30,
        Guid[]? causalParents = null,
        string? source = null,
        Guid? sessionId = null,
        string userId = "default_user",
        string[]? tags = null,
        string? createdBy = null)
    {
        var aggregate = new MemoryAggregate();
        var streamId = Guid.CreateVersion7();

        var @event = new MemoryCreatedEvent
        {
            StreamId = streamId,
            EventVersion = 1,
            Content = content,
            Layer = layer,
            Embedding = embedding,
            ConfidenceScore = confidenceScore,
            HalfLifeDays = halfLifeDays,
            CausalParents = causalParents ?? [],
            Source = source,
            SessionId = sessionId,
            UserId = userId,
            Tags = tags ?? [],
            CreatedBy = createdBy
        };

        aggregate.Apply(@event);
        aggregate._uncommittedEvents.Add(@event);
        return aggregate;
    }

    public void Update(string newContent, float[]? newEmbedding = null, string? reason = null, string? updatedBy = null)
    {
        if (!IsActive) throw new InvalidOperationException("Cannot update inactive memory");

        var @event = new MemoryUpdatedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            NewContent = newContent,
            PreviousContentHash = ContentHash,
            NewEmbedding = newEmbedding,
            Reason = reason,
            CreatedBy = updatedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Reinforce(float newConfidence, string source, Guid[]? validatedByIds = null, string? reinforcedBy = null)
    {
        if (!IsActive) throw new InvalidOperationException("Cannot reinforce inactive memory");

        var @event = new MemoryReinforcedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            PreviousConfidence = ConfidenceScore,
            NewConfidence = Math.Clamp(newConfidence, 0f, 1f),
            ReinforcementSource = source,
            ValidatedByIds = validatedByIds ?? [],
            CreatedBy = reinforcedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void ApplyDecay(string? decayedBy = null)
    {
        if (!IsActive) return;

        var currentConfidence = CurrentConfidence;
        if (Math.Abs(currentConfidence - ConfidenceScore) < 0.001f) return;

        var daysSince = (int)(DateTimeOffset.UtcNow - LastReinforcedAt).TotalDays;

        var @event = new MemoryDecayedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            PreviousConfidence = ConfidenceScore,
            NewConfidence = currentConfidence,
            DaysSinceReinforcement = daysSince,
            CreatedBy = decayedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void TransitionLayer(MemoryLayer newLayer, string? reason = null, Guid? triggeredBy = null, string? transitionedBy = null)
    {
        if (!IsActive) throw new InvalidOperationException("Cannot transition inactive memory");
        if (Layer == newLayer) return;

        var @event = new MemoryLayerTransitionedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            PreviousLayer = Layer,
            NewLayer = newLayer,
            TransitionReason = reason,
            TriggeredByMemoryId = triggeredBy,
            CreatedBy = transitionedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Invalidate(string reason, Guid? supersededById = null, Guid[]? contradictedByIds = null, string? invalidatedBy = null)
    {
        if (!IsActive) return;

        var @event = new MemoryInvalidatedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            Reason = reason,
            SupersededById = supersededById,
            ContradictedByIds = contradictedByIds ?? [],
            CreatedBy = invalidatedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Merge(Guid[] sourceMemoryIds, string mergedContent, float[]? mergedEmbedding = null, string? strategy = null, string? mergedBy = null)
    {
        var @event = new MemoryMergedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            SourceMemoryIds = sourceMemoryIds,
            MergedContent = mergedContent,
            MergedEmbedding = mergedEmbedding,
            MergeStrategy = strategy,
            CreatedBy = mergedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Archive(string reason, int accessCount, DateTimeOffset lastAccessedAt, string? archivedBy = null)
    {
        if (!IsActive || IsArchived) return;

        var daysSinceAccess = (int)(DateTimeOffset.UtcNow - lastAccessedAt).TotalDays;

        var @event = new MemoryArchivedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            Reason = reason,
            ConfidenceAtArchive = CurrentConfidence,
            AccessCountAtArchive = accessCount,
            DaysSinceLastAccess = daysSinceAccess,
            CreatedBy = archivedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Recall(string? query, float similarityScore, string? context, Guid? sessionId, string? recalledBy = null)
    {
        if (!IsActive) return;

        var @event = new MemoryRecalledEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            Query = query,
            SimilarityScore = similarityScore,
            RecallContext = context,
            SessionId = sessionId,
            CreatedBy = recalledBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Ignore(string? query, string reason, Guid? sessionId, string? ignoredBy = null)
    {
        if (!IsActive) return;

        var @event = new MemoryIgnoredEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            Query = query,
            Reason = reason,
            SessionId = sessionId,
            CreatedBy = ignoredBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void MarkContradiction(Guid[] contradictingMemoryIds, string contradictionType, string? detectionMethod = null, float confidence = 1.0f, string? markedBy = null)
    {
        if (!IsActive) return;

        var @event = new MemoryContradictedEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            ContradictingMemoryIds = contradictingMemoryIds,
            ContradictionType = contradictionType,
            DetectionMethod = detectionMethod,
            ContradictionConfidence = confidence,
            CreatedBy = markedBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Expire(string policy, int originalTtlDays, string? expiredBy = null)
    {
        if (!IsActive || IsExpired) return;

        var @event = new MemoryExpiredEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            ExpirationPolicy = policy,
            OriginalTtlDays = originalTtlDays,
            ConfidenceAtExpiration = CurrentConfidence,
            AccessCountAtExpiration = RecallCount,
            CreatedBy = expiredBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    public void Split(Guid[] childMemoryIds, string? strategy = null, string? reason = null, string? splitBy = null)
    {
        if (!IsActive || IsSplit) return;

        var @event = new MemorySplitEvent
        {
            StreamId = Id,
            EventVersion = Version + 1,
            ChildMemoryIds = childMemoryIds,
            SplitStrategy = strategy,
            Reason = reason,
            CreatedBy = splitBy
        };

        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    // ========================================================================
    // EVENT APPLICATION (rebuild state)
    // ========================================================================

    public void Apply(IMemoryEvent @event)
    {
        switch (@event)
        {
            case MemoryCreatedEvent e:
                Apply(e);
                break;
            case MemoryUpdatedEvent e:
                Apply(e);
                break;
            case MemoryReinforcedEvent e:
                Apply(e);
                break;
            case MemoryDecayedEvent e:
                Apply(e);
                break;
            case MemoryLayerTransitionedEvent e:
                Apply(e);
                break;
            case MemoryInvalidatedEvent e:
                Apply(e);
                break;
            case MemoryMergedEvent e:
                Apply(e);
                break;
            case MemoryArchivedEvent e:
                Apply(e);
                break;
            case MemoryRecalledEvent e:
                Apply(e);
                break;
            case MemoryIgnoredEvent e:
                Apply(e);
                break;
            case MemoryContradictedEvent e:
                Apply(e);
                break;
            case MemoryExpiredEvent e:
                Apply(e);
                break;
            case MemorySplitEvent e:
                Apply(e);
                break;
            default:
                throw new InvalidOperationException($"Unknown event type: {@event.GetType().Name}");
        }
    }

    private void Apply(MemoryCreatedEvent e)
    {
        Id = e.StreamId;
        Content = e.Content;
        ContentHash = ComputeHash(e.Content);
        Embedding = e.Embedding;
        Layer = e.Layer;
        ConfidenceScore = e.ConfidenceScore;
        HalfLifeDays = e.HalfLifeDays;
        CausalParents = e.CausalParents;
        Source = e.Source;
        SessionId = e.SessionId;
        UserId = e.UserId;
        Tags = e.Tags;
        LastReinforcedAt = e.CreatedAt;
        CreatedAt = e.CreatedAt;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
        IsActive = true;
    }

    private void Apply(MemoryUpdatedEvent e)
    {
        Content = e.NewContent;
        ContentHash = ComputeHash(e.NewContent);
        if (e.NewEmbedding != null) Embedding = e.NewEmbedding;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryReinforcedEvent e)
    {
        ConfidenceScore = e.NewConfidence;
        LastReinforcedAt = e.CreatedAt;
        ValidatedBy = [.. ValidatedBy, .. e.ValidatedByIds];
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryDecayedEvent e)
    {
        ConfidenceScore = e.NewConfidence;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryLayerTransitionedEvent e)
    {
        Layer = e.NewLayer;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryInvalidatedEvent e)
    {
        IsActive = false;
        ContradictionIds = [.. ContradictionIds, .. e.ContradictedByIds];
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryMergedEvent e)
    {
        Content = e.MergedContent;
        ContentHash = ComputeHash(e.MergedContent);
        if (e.MergedEmbedding != null) Embedding = e.MergedEmbedding;
        CausalParents = [.. CausalParents, .. e.SourceMemoryIds];
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryArchivedEvent e)
    {
        IsArchived = true;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryRecalledEvent e)
    {
        RecallCount++;
        LastRecalledAt = e.CreatedAt;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryIgnoredEvent e)
    {
        IgnoreCount++;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryContradictedEvent e)
    {
        ContradictionIds = [.. ContradictionIds, .. e.ContradictingMemoryIds];
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemoryExpiredEvent e)
    {
        IsExpired = true;
        IsActive = false;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    private void Apply(MemorySplitEvent e)
    {
        IsSplit = true;
        IsActive = false;
        ChildMemoryIds = e.ChildMemoryIds;
        UpdatedAt = e.CreatedAt;
        Version = e.EventVersion;
    }

    /// <summary>
    /// Rebuild aggregate from event stream.
    /// </summary>
    public static MemoryAggregate FromEvents(IEnumerable<IMemoryEvent> events)
    {
        var aggregate = new MemoryAggregate();
        foreach (var @event in events.OrderBy(e => e.EventVersion))
        {
            aggregate.Apply(@event);
        }
        return aggregate;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Verify content integrity by checking hash.
    /// </summary>
    public bool VerifyIntegrity()
    {
        return ContentHash == ComputeHash(Content);
    }
}
