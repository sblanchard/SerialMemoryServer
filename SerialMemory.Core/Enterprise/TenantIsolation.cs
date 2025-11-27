using System.Security.Cryptography;
using System.Text;

namespace SerialMemory.Core.Enterprise;

/// <summary>
/// Tenant context for multi-tenant isolation.
/// Flows through all operations to enforce boundaries.
/// </summary>
public sealed class TenantContext
{
    private static readonly AsyncLocal<TenantContext?> _current = new();

    public string TenantId { get; }
    public string? UserId { get; init; }
    public TenantTier Tier { get; init; } = TenantTier.Standard;
    public TenantLimits Limits { get; init; } = TenantLimits.Default;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public string CorrelationId { get; } = Guid.CreateVersion7().ToString();

    private TenantContext(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID cannot be null or empty", nameof(tenantId));

        TenantId = tenantId;
    }

    /// <summary>Current tenant context (thread/async-local).</summary>
    public static TenantContext? Current => _current.Value;

    /// <summary>Get current tenant or throw if not set.</summary>
    public static TenantContext Required =>
        _current.Value ?? throw new TenantContextMissingException();

    /// <summary>Create a new tenant context scope.</summary>
    public static IDisposable CreateScope(string tenantId, string? userId = null, TenantTier? tier = null)
    {
        var context = new TenantContext(tenantId)
        {
            UserId = userId,
            Tier = tier ?? TenantTier.Standard,
            Limits = TenantLimits.ForTier(tier ?? TenantTier.Standard)
        };
        return new TenantScope(context);
    }

    /// <summary>Create a system context (bypasses tenant checks).</summary>
    public static IDisposable CreateSystemScope()
    {
        var context = new TenantContext("__system__")
        {
            Tier = TenantTier.System,
            Limits = TenantLimits.Unlimited
        };
        return new TenantScope(context);
    }

    private sealed class TenantScope : IDisposable
    {
        private readonly TenantContext? _previous;

        public TenantScope(TenantContext context)
        {
            _previous = _current.Value;
            _current.Value = context;
        }

        public void Dispose()
        {
            _current.Value = _previous;
        }
    }
}

public enum TenantTier
{
    Free,
    Standard,
    Professional,
    Enterprise,
    System  // Internal system operations
}

/// <summary>
/// Tenant-specific resource limits.
/// </summary>
public sealed class TenantLimits
{
    public int MaxMemories { get; init; }
    public int MaxEntities { get; init; }
    public int MaxRelationships { get; init; }
    public int MaxBatchSize { get; init; }
    public int MaxQueryResults { get; init; }
    public int MaxContentLength { get; init; }
    public TimeSpan RateLimitWindow { get; init; }
    public int RateLimitRequests { get; init; }
    public bool AllowExport { get; init; }
    public bool AllowBulkOperations { get; init; }
    public bool AllowRawSql { get; init; }

    public static TenantLimits Default => ForTier(TenantTier.Standard);

    public static TenantLimits Unlimited => new()
    {
        MaxMemories = int.MaxValue,
        MaxEntities = int.MaxValue,
        MaxRelationships = int.MaxValue,
        MaxBatchSize = int.MaxValue,
        MaxQueryResults = int.MaxValue,
        MaxContentLength = int.MaxValue,
        RateLimitWindow = TimeSpan.Zero,
        RateLimitRequests = int.MaxValue,
        AllowExport = true,
        AllowBulkOperations = true,
        AllowRawSql = true
    };

    public static TenantLimits ForTier(TenantTier tier) => tier switch
    {
        TenantTier.Free => new TenantLimits
        {
            MaxMemories = 1_000,
            MaxEntities = 500,
            MaxRelationships = 2_000,
            MaxBatchSize = 10,
            MaxQueryResults = 50,
            MaxContentLength = 10_000,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 60,
            AllowExport = false,
            AllowBulkOperations = false,
            AllowRawSql = false
        },
        TenantTier.Standard => new TenantLimits
        {
            MaxMemories = 50_000,
            MaxEntities = 10_000,
            MaxRelationships = 100_000,
            MaxBatchSize = 100,
            MaxQueryResults = 500,
            MaxContentLength = 100_000,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 300,
            AllowExport = true,
            AllowBulkOperations = true,
            AllowRawSql = false
        },
        TenantTier.Professional => new TenantLimits
        {
            MaxMemories = 500_000,
            MaxEntities = 100_000,
            MaxRelationships = 1_000_000,
            MaxBatchSize = 1_000,
            MaxQueryResults = 2_000,
            MaxContentLength = 500_000,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 1_000,
            AllowExport = true,
            AllowBulkOperations = true,
            AllowRawSql = false
        },
        TenantTier.Enterprise => new TenantLimits
        {
            MaxMemories = 10_000_000,
            MaxEntities = 1_000_000,
            MaxRelationships = 10_000_000,
            MaxBatchSize = 10_000,
            MaxQueryResults = 10_000,
            MaxContentLength = 1_000_000,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 10_000,
            AllowExport = true,
            AllowBulkOperations = true,
            AllowRawSql = true
        },
        TenantTier.System => Unlimited,
        _ => Default
    };
}

/// <summary>
/// Tenant isolation guard - enforces tenant boundaries.
/// </summary>
public sealed class TenantGuard
{
    /// <summary>Ensure current context matches resource tenant.</summary>
    public static void EnsureTenantMatch(string resourceTenantId)
    {
        var context = TenantContext.Current;
        if (context == null)
            throw new TenantContextMissingException();

        if (context.Tier == TenantTier.System)
            return; // System context bypasses checks

        if (context.TenantId != resourceTenantId)
            throw new TenantIsolationViolationException(context.TenantId, resourceTenantId);
    }

    /// <summary>Ensure operation is within rate limits.</summary>
    public static void EnsureRateLimit(TenantRateLimitTracker tracker)
    {
        var context = TenantContext.Required;
        if (context.Tier == TenantTier.System)
            return;

        if (!tracker.TryAcquire(context.TenantId, context.Limits))
            throw new TenantRateLimitExceededException(context.TenantId);
    }

    /// <summary>Ensure content length is within limits.</summary>
    public static void EnsureContentLength(int length)
    {
        var context = TenantContext.Required;
        if (context.Tier == TenantTier.System)
            return;

        if (length > context.Limits.MaxContentLength)
            throw new TenantLimitExceededException(context.TenantId, "ContentLength", length, context.Limits.MaxContentLength);
    }

    /// <summary>Ensure batch size is within limits.</summary>
    public static void EnsureBatchSize(int size)
    {
        var context = TenantContext.Required;
        if (context.Tier == TenantTier.System)
            return;

        if (size > context.Limits.MaxBatchSize)
            throw new TenantLimitExceededException(context.TenantId, "BatchSize", size, context.Limits.MaxBatchSize);
    }

    /// <summary>Ensure feature is allowed for tenant tier.</summary>
    public static void EnsureFeatureAllowed(string feature)
    {
        var context = TenantContext.Required;
        if (context.Tier == TenantTier.System)
            return;

        var allowed = feature.ToLowerInvariant() switch
        {
            "export" => context.Limits.AllowExport,
            "bulk" or "bulk_operations" => context.Limits.AllowBulkOperations,
            "raw_sql" or "sql" => context.Limits.AllowRawSql,
            _ => true
        };

        if (!allowed)
            throw new TenantFeatureNotAllowedException(context.TenantId, feature, context.Tier);
    }
}

/// <summary>
/// Rate limit tracker per tenant.
/// </summary>
public sealed class TenantRateLimitTracker
{
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> _windows = new();
    private readonly object _lock = new();

    public bool TryAcquire(string tenantId, TenantLimits limits)
    {
        if (limits.RateLimitWindow == TimeSpan.Zero)
            return true;

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (!_windows.TryGetValue(tenantId, out var window) ||
                now - window.WindowStart >= limits.RateLimitWindow)
            {
                // New window
                _windows[tenantId] = (now, 1);
                return true;
            }

            if (window.Count >= limits.RateLimitRequests)
            {
                return false;
            }

            _windows[tenantId] = (window.WindowStart, window.Count + 1);
            return true;
        }
    }

    public (DateTimeOffset WindowStart, int Count)? GetWindow(string tenantId)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(tenantId, out var window) ? window : null;
        }
    }

    public void Reset(string tenantId)
    {
        lock (_lock)
        {
            _windows.Remove(tenantId);
        }
    }
}

/// <summary>
/// Tenant-aware SQL filter generator for RLS-style filtering.
/// </summary>
public static class TenantSqlFilter
{
    /// <summary>Generate WHERE clause for tenant filtering.</summary>
    public static string WhereClause(string tenantColumn = "tenant_id")
    {
        var context = TenantContext.Current;
        if (context == null || context.Tier == TenantTier.System)
            return "1=1"; // No filter for system context

        return $"{tenantColumn} = @TenantId";
    }

    /// <summary>Get tenant ID parameter value.</summary>
    public static object GetTenantParameter()
    {
        var context = TenantContext.Required;
        return new { TenantId = context.TenantId };
    }

    /// <summary>Generate PostgreSQL RLS policy SQL.</summary>
    public static string GenerateRlsPolicy(string tableName, string tenantColumn = "tenant_id")
    {
        return $"""
            -- Enable RLS on {tableName}
            ALTER TABLE {tableName} ENABLE ROW LEVEL SECURITY;

            -- Create policy for tenant isolation
            DROP POLICY IF EXISTS tenant_isolation ON {tableName};
            CREATE POLICY tenant_isolation ON {tableName}
                USING ({tenantColumn} = current_setting('app.tenant_id', true))
                WITH CHECK ({tenantColumn} = current_setting('app.tenant_id', true));

            -- Force RLS for table owner too
            ALTER TABLE {tableName} FORCE ROW LEVEL SECURITY;
            """;
    }
}

/// <summary>
/// Tenant data encryption for sensitive fields.
/// </summary>
public sealed class TenantEncryption
{
    private readonly byte[] _masterKey;

    public TenantEncryption(byte[] masterKey)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes (256 bits)", nameof(masterKey));
        _masterKey = masterKey;
    }

    /// <summary>Derive tenant-specific key from master key.</summary>
    public byte[] DeriveKey(string tenantId)
    {
        using var hmac = new HMACSHA256(_masterKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"tenant:{tenantId}"));
    }

    /// <summary>Encrypt data for specific tenant.</summary>
    public byte[] Encrypt(string tenantId, byte[] plaintext)
    {
        var key = DeriveKey(tenantId);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ciphertext, 0, result, aes.IV.Length, ciphertext.Length);
        return result;
    }

    /// <summary>Decrypt data for specific tenant.</summary>
    public byte[] Decrypt(string tenantId, byte[] ciphertext)
    {
        var key = DeriveKey(tenantId);
        using var aes = Aes.Create();
        aes.Key = key;

        // Extract IV from beginning of ciphertext
        var iv = new byte[16];
        Buffer.BlockCopy(ciphertext, 0, iv, 0, 16);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 16, ciphertext.Length - 16);
    }
}

// Exception types
public sealed class TenantContextMissingException : Exception
{
    public TenantContextMissingException()
        : base("Tenant context is required but not set. Use TenantContext.CreateScope() to establish context.") { }
}

public sealed class TenantIsolationViolationException : Exception
{
    public string RequestingTenant { get; }
    public string ResourceTenant { get; }

    public TenantIsolationViolationException(string requestingTenant, string resourceTenant)
        : base($"Tenant isolation violation: tenant '{requestingTenant}' attempted to access resource belonging to tenant '{resourceTenant}'")
    {
        RequestingTenant = requestingTenant;
        ResourceTenant = resourceTenant;
    }
}

public sealed class TenantRateLimitExceededException : Exception
{
    public string TenantId { get; }

    public TenantRateLimitExceededException(string tenantId)
        : base($"Rate limit exceeded for tenant '{tenantId}'")
    {
        TenantId = tenantId;
    }
}

public sealed class TenantLimitExceededException : Exception
{
    public string TenantId { get; }
    public string LimitName { get; }
    public int AttemptedValue { get; }
    public int MaxValue { get; }

    public TenantLimitExceededException(string tenantId, string limitName, int attemptedValue, int maxValue)
        : base($"Tenant '{tenantId}' exceeded {limitName} limit: {attemptedValue} > {maxValue}")
    {
        TenantId = tenantId;
        LimitName = limitName;
        AttemptedValue = attemptedValue;
        MaxValue = maxValue;
    }
}

public sealed class TenantFeatureNotAllowedException : Exception
{
    public string TenantId { get; }
    public string Feature { get; }
    public TenantTier Tier { get; }

    public TenantFeatureNotAllowedException(string tenantId, string feature, TenantTier tier)
        : base($"Feature '{feature}' is not allowed for tenant '{tenantId}' on tier '{tier}'")
    {
        TenantId = tenantId;
        Feature = feature;
        Tier = tier;
    }
}
