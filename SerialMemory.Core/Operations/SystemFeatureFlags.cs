namespace SerialMemory.Core.Operations;

/// <summary>
/// Feature flags for optional subsystems.
/// All subsystems start in observe-only mode - no destructive actions.
/// Flags controlled by ENV variables, tenant tier, and deployment mode.
/// </summary>
public sealed class SystemFeatureFlags
{
    // Infrastructure Engines
    public bool EmbeddingCacheEnabled { get; init; }
    public bool DeterministicInferenceEnabled { get; init; }
    public bool MemoryCompilationEnabled { get; init; }
    public bool ContextOptimizationEnabled { get; init; }
    public bool LocalEncryptionEnabled { get; init; }
    public bool DualPassReasoningEnabled { get; init; }
    public bool GraphRecrawlEnabled { get; init; }
    public bool TimeTravelEnabled { get; init; }

    // Observe-only mode (safe activation)
    public bool ObserveOnlyMode { get; init; } = true;

    // Tier-based activation thresholds
    public TenantTier MinimumTierForAdvancedFeatures { get; init; } = TenantTier.Pro;

    /// <summary>
    /// Creates flags from environment variables with safe defaults.
    /// All features default to OFF for safety.
    /// </summary>
    public static SystemFeatureFlags FromEnvironment()
    {
        return new SystemFeatureFlags
        {
            // Infrastructure engines - all OFF by default
            EmbeddingCacheEnabled = GetBoolEnv("FEATURE_EMBEDDING_CACHE"),
            DeterministicInferenceEnabled = GetBoolEnv("FEATURE_DETERMINISTIC_INFERENCE"),
            MemoryCompilationEnabled = GetBoolEnv("FEATURE_MEMORY_COMPILATION"),
            ContextOptimizationEnabled = GetBoolEnv("FEATURE_CONTEXT_OPTIMIZATION"),
            LocalEncryptionEnabled = GetBoolEnv("FEATURE_LOCAL_ENCRYPTION"),
            DualPassReasoningEnabled = GetBoolEnv("FEATURE_DUAL_PASS_REASONING"),
            GraphRecrawlEnabled = GetBoolEnv("FEATURE_GRAPH_RECRAWL"),
            TimeTravelEnabled = GetBoolEnv("FEATURE_TIME_TRAVEL"),

            // Observe-only mode - ON by default for safety
            ObserveOnlyMode = GetBoolEnv("FEATURE_OBSERVE_ONLY_MODE", defaultValue: true),

            // Tier threshold
            MinimumTierForAdvancedFeatures = ParseTier(
                Environment.GetEnvironmentVariable("FEATURE_MIN_TIER") ?? "pro")
        };
    }

    /// <summary>
    /// Creates flags with all features enabled (for testing).
    /// Still respects observe-only mode.
    /// </summary>
    public static SystemFeatureFlags AllEnabled(bool observeOnly = true)
    {
        return new SystemFeatureFlags
        {
            EmbeddingCacheEnabled = true,
            DeterministicInferenceEnabled = true,
            MemoryCompilationEnabled = true,
            ContextOptimizationEnabled = true,
            LocalEncryptionEnabled = true,
            DualPassReasoningEnabled = true,
            GraphRecrawlEnabled = true,
            TimeTravelEnabled = true,
            ObserveOnlyMode = observeOnly,
            MinimumTierForAdvancedFeatures = TenantTier.Free
        };
    }

    /// <summary>
    /// Creates flags with all features disabled (safe default).
    /// </summary>
    public static SystemFeatureFlags AllDisabled()
    {
        return new SystemFeatureFlags
        {
            EmbeddingCacheEnabled = false,
            DeterministicInferenceEnabled = false,
            MemoryCompilationEnabled = false,
            ContextOptimizationEnabled = false,
            LocalEncryptionEnabled = false,
            DualPassReasoningEnabled = false,
            GraphRecrawlEnabled = false,
            TimeTravelEnabled = false,
            ObserveOnlyMode = true,
            MinimumTierForAdvancedFeatures = TenantTier.Enterprise
        };
    }

    /// <summary>
    /// Checks if a feature is enabled for the given tenant tier.
    /// </summary>
    public bool IsFeatureEnabledForTier(string featureName, TenantTier tenantTier)
    {
        // Basic features available to all tiers
        var basicFeatures = new[] { "EmbeddingCache", "ContextOptimization" };

        var isBasic = basicFeatures.Contains(featureName, StringComparer.OrdinalIgnoreCase);
        if (isBasic) return IsFeatureEnabled(featureName);

        // Advanced features require minimum tier
        if (tenantTier < MinimumTierForAdvancedFeatures) return false;

        return IsFeatureEnabled(featureName);
    }

    /// <summary>
    /// Checks if a specific feature is enabled by name.
    /// </summary>
    public bool IsFeatureEnabled(string featureName)
    {
        return featureName.ToLowerInvariant() switch
        {
            "embeddingcache" => EmbeddingCacheEnabled,
            "deterministicinference" => DeterministicInferenceEnabled,
            "memorycompilation" => MemoryCompilationEnabled,
            "contextoptimization" => ContextOptimizationEnabled,
            "localencryption" => LocalEncryptionEnabled,
            "dualpassreasoning" => DualPassReasoningEnabled,
            "graphrecrawl" => GraphRecrawlEnabled,
            "timetravel" => TimeTravelEnabled,
            _ => false
        };
    }

    /// <summary>
    /// Returns list of all enabled features.
    /// </summary>
    public IReadOnlyList<string> GetEnabledFeatures()
    {
        var enabled = new List<string>();
        if (EmbeddingCacheEnabled) enabled.Add("EmbeddingCache");
        if (DeterministicInferenceEnabled) enabled.Add("DeterministicInference");
        if (MemoryCompilationEnabled) enabled.Add("MemoryCompilation");
        if (ContextOptimizationEnabled) enabled.Add("ContextOptimization");
        if (LocalEncryptionEnabled) enabled.Add("LocalEncryption");
        if (DualPassReasoningEnabled) enabled.Add("DualPassReasoning");
        if (GraphRecrawlEnabled) enabled.Add("GraphRecrawl");
        if (TimeTravelEnabled) enabled.Add("TimeTravel");
        return enabled;
    }

    /// <summary>
    /// Returns status summary for logging/diagnostics.
    /// </summary>
    public string GetStatusSummary()
    {
        var enabled = GetEnabledFeatures();
        var mode = ObserveOnlyMode ? "OBSERVE-ONLY" : "ACTIVE";

        return enabled.Count > 0
            ? $"Features [{mode}]: {string.Join(", ", enabled)}"
            : $"No optional features enabled [{mode}]";
    }

    private static bool GetBoolEnv(string name, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)) return defaultValue;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static TenantTier ParseTier(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "free" => TenantTier.Free,
            "starter" => TenantTier.Starter,
            "pro" => TenantTier.Pro,
            "enterprise" => TenantTier.Enterprise,
            _ => TenantTier.Pro
        };
    }
}

/// <summary>
/// Tenant subscription tiers for feature gating.
/// </summary>
public enum TenantTier
{
    Free = 0,
    Starter = 1,
    Pro = 2,
    Enterprise = 3
}
