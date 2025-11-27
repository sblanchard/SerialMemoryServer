using SerialMemory.Core.Enterprise;
using Xunit;

namespace SerialMemory.Tests.Enterprise;

public class TenantIsolationTests
{
    [Fact]
    public void TenantContext_CreateScope_SetsCurrent()
    {
        Assert.Null(TenantContext.Current);

        using (TenantContext.CreateScope("tenant1"))
        {
            Assert.NotNull(TenantContext.Current);
            Assert.Equal("tenant1", TenantContext.Current.TenantId);
        }

        Assert.Null(TenantContext.Current);
    }

    [Fact]
    public void TenantContext_NestedScopes_RestoresPrevious()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            Assert.Equal("tenant1", TenantContext.Current!.TenantId);

            using (TenantContext.CreateScope("tenant2"))
            {
                Assert.Equal("tenant2", TenantContext.Current!.TenantId);
            }

            Assert.Equal("tenant1", TenantContext.Current!.TenantId);
        }
    }

    [Fact]
    public void TenantContext_Required_ThrowsWhenMissing()
    {
        Assert.Throws<TenantContextMissingException>(() => TenantContext.Required);
    }

    [Fact]
    public void TenantContext_Required_ReturnsContextWhenSet()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var context = TenantContext.Required;
            Assert.Equal("tenant1", context.TenantId);
        }
    }

    [Fact]
    public void TenantContext_SystemScope_HasSystemTier()
    {
        using (TenantContext.CreateSystemScope())
        {
            Assert.Equal("__system__", TenantContext.Current!.TenantId);
            Assert.Equal(TenantTier.System, TenantContext.Current!.Tier);
        }
    }

    [Fact]
    public void TenantContext_CreateScope_SetsTier()
    {
        using (TenantContext.CreateScope("tenant1", tier: TenantTier.Enterprise))
        {
            Assert.Equal(TenantTier.Enterprise, TenantContext.Current!.Tier);
        }
    }

    [Fact]
    public void TenantContext_CreateScope_SetsUserId()
    {
        using (TenantContext.CreateScope("tenant1", userId: "user123"))
        {
            Assert.Equal("user123", TenantContext.Current!.UserId);
        }
    }

    [Fact]
    public void TenantContext_HasCorrelationId()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            Assert.NotNull(TenantContext.Current!.CorrelationId);
            Assert.NotEmpty(TenantContext.Current!.CorrelationId);
        }
    }

    // ==========================================
    // TENANT LIMITS TESTS
    // ==========================================

    [Theory]
    [InlineData(TenantTier.Free, 1000)]
    [InlineData(TenantTier.Standard, 50000)]
    [InlineData(TenantTier.Professional, 500000)]
    [InlineData(TenantTier.Enterprise, 10000000)]
    public void TenantLimits_ForTier_ReturnsCorrectMaxMemories(TenantTier tier, int expectedMax)
    {
        var limits = TenantLimits.ForTier(tier);
        Assert.Equal(expectedMax, limits.MaxMemories);
    }

    [Fact]
    public void TenantLimits_FreeTier_DisallowsExport()
    {
        var limits = TenantLimits.ForTier(TenantTier.Free);
        Assert.False(limits.AllowExport);
    }

    [Fact]
    public void TenantLimits_EnterpriseTier_AllowsRawSql()
    {
        var limits = TenantLimits.ForTier(TenantTier.Enterprise);
        Assert.True(limits.AllowRawSql);
    }

    [Fact]
    public void TenantLimits_Unlimited_HasMaxValues()
    {
        var limits = TenantLimits.Unlimited;
        Assert.Equal(int.MaxValue, limits.MaxMemories);
        Assert.True(limits.AllowRawSql);
    }

    // ==========================================
    // TENANT GUARD TESTS
    // ==========================================

    [Fact]
    public void TenantGuard_EnsureTenantMatch_SameTenant_NoException()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var ex = Record.Exception(() => TenantGuard.EnsureTenantMatch("tenant1"));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TenantGuard_EnsureTenantMatch_DifferentTenant_ThrowsViolation()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var ex = Assert.Throws<TenantIsolationViolationException>(
                () => TenantGuard.EnsureTenantMatch("tenant2"));

            Assert.Equal("tenant1", ex.RequestingTenant);
            Assert.Equal("tenant2", ex.ResourceTenant);
        }
    }

    [Fact]
    public void TenantGuard_EnsureTenantMatch_SystemContext_BypassesCheck()
    {
        using (TenantContext.CreateSystemScope())
        {
            var ex = Record.Exception(() => TenantGuard.EnsureTenantMatch("any-tenant"));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TenantGuard_EnsureTenantMatch_NoContext_Throws()
    {
        Assert.Throws<TenantContextMissingException>(
            () => TenantGuard.EnsureTenantMatch("tenant1"));
    }

    [Fact]
    public void TenantGuard_EnsureContentLength_WithinLimit_NoException()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var ex = Record.Exception(() => TenantGuard.EnsureContentLength(1000));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TenantGuard_EnsureContentLength_ExceedsLimit_ThrowsException()
    {
        using (TenantContext.CreateScope("tenant1", tier: TenantTier.Free))
        {
            var ex = Assert.Throws<TenantLimitExceededException>(
                () => TenantGuard.EnsureContentLength(1_000_000)); // Free tier limit is 10,000

            Assert.Equal("tenant1", ex.TenantId);
            Assert.Equal("ContentLength", ex.LimitName);
        }
    }

    [Fact]
    public void TenantGuard_EnsureBatchSize_WithinLimit_NoException()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var ex = Record.Exception(() => TenantGuard.EnsureBatchSize(50));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TenantGuard_EnsureBatchSize_ExceedsLimit_ThrowsException()
    {
        using (TenantContext.CreateScope("tenant1", tier: TenantTier.Free))
        {
            var ex = Assert.Throws<TenantLimitExceededException>(
                () => TenantGuard.EnsureBatchSize(100)); // Free tier limit is 10

            Assert.Equal("BatchSize", ex.LimitName);
        }
    }

    [Fact]
    public void TenantGuard_EnsureFeatureAllowed_AllowedFeature_NoException()
    {
        using (TenantContext.CreateScope("tenant1", tier: TenantTier.Enterprise))
        {
            var ex = Record.Exception(() => TenantGuard.EnsureFeatureAllowed("export"));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TenantGuard_EnsureFeatureAllowed_DisallowedFeature_ThrowsException()
    {
        using (TenantContext.CreateScope("tenant1", tier: TenantTier.Free))
        {
            var ex = Assert.Throws<TenantFeatureNotAllowedException>(
                () => TenantGuard.EnsureFeatureAllowed("export"));

            Assert.Equal("export", ex.Feature);
            Assert.Equal(TenantTier.Free, ex.Tier);
        }
    }

    // ==========================================
    // RATE LIMITER TESTS
    // ==========================================

    [Fact]
    public void RateLimitTracker_FirstRequest_Succeeds()
    {
        var tracker = new TenantRateLimitTracker();
        var limits = TenantLimits.ForTier(TenantTier.Standard);

        var result = tracker.TryAcquire("tenant1", limits);

        Assert.True(result);
    }

    [Fact]
    public void RateLimitTracker_WithinLimit_Succeeds()
    {
        var tracker = new TenantRateLimitTracker();
        var limits = new TenantLimits
        {
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 10
        };

        for (int i = 0; i < 10; i++)
        {
            Assert.True(tracker.TryAcquire("tenant1", limits));
        }
    }

    [Fact]
    public void RateLimitTracker_ExceedsLimit_Fails()
    {
        var tracker = new TenantRateLimitTracker();
        var limits = new TenantLimits
        {
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 5
        };

        for (int i = 0; i < 5; i++)
        {
            tracker.TryAcquire("tenant1", limits);
        }

        var result = tracker.TryAcquire("tenant1", limits);
        Assert.False(result);
    }

    [Fact]
    public void RateLimitTracker_Reset_AllowsNewRequests()
    {
        var tracker = new TenantRateLimitTracker();
        var limits = new TenantLimits
        {
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitRequests = 1
        };

        tracker.TryAcquire("tenant1", limits);
        Assert.False(tracker.TryAcquire("tenant1", limits));

        tracker.Reset("tenant1");

        Assert.True(tracker.TryAcquire("tenant1", limits));
    }

    [Fact]
    public void RateLimitTracker_GetWindow_ReturnsCurrentWindow()
    {
        var tracker = new TenantRateLimitTracker();
        var limits = TenantLimits.ForTier(TenantTier.Standard);

        tracker.TryAcquire("tenant1", limits);
        tracker.TryAcquire("tenant1", limits);

        var window = tracker.GetWindow("tenant1");

        Assert.NotNull(window);
        Assert.Equal(2, window.Value.Count);
    }

    // ==========================================
    // SQL FILTER TESTS
    // ==========================================

    [Fact]
    public void TenantSqlFilter_WhereClause_ReturnsFilter()
    {
        using (TenantContext.CreateScope("tenant1"))
        {
            var clause = TenantSqlFilter.WhereClause();
            Assert.Equal("tenant_id = @TenantId", clause);
        }
    }

    [Fact]
    public void TenantSqlFilter_WhereClause_SystemContext_ReturnsNoFilter()
    {
        using (TenantContext.CreateSystemScope())
        {
            var clause = TenantSqlFilter.WhereClause();
            Assert.Equal("1=1", clause);
        }
    }

    [Fact]
    public void TenantSqlFilter_WhereClause_NoContext_ReturnsNoFilter()
    {
        var clause = TenantSqlFilter.WhereClause();
        Assert.Equal("1=1", clause);
    }

    // ==========================================
    // ENCRYPTION TESTS
    // ==========================================

    [Fact]
    public void TenantEncryption_DeriveKey_DifferentForDifferentTenants()
    {
        var masterKey = new byte[32];
        new Random(42).NextBytes(masterKey);
        var encryption = new TenantEncryption(masterKey);

        var key1 = encryption.DeriveKey("tenant1");
        var key2 = encryption.DeriveKey("tenant2");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void TenantEncryption_DeriveKey_ConsistentForSameTenant()
    {
        var masterKey = new byte[32];
        new Random(42).NextBytes(masterKey);
        var encryption = new TenantEncryption(masterKey);

        var key1 = encryption.DeriveKey("tenant1");
        var key2 = encryption.DeriveKey("tenant1");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void TenantEncryption_EncryptDecrypt_RoundTrip()
    {
        var masterKey = new byte[32];
        new Random(42).NextBytes(masterKey);
        var encryption = new TenantEncryption(masterKey);

        var plaintext = "Hello, World!"u8.ToArray();
        var ciphertext = encryption.Encrypt("tenant1", plaintext);
        var decrypted = encryption.Decrypt("tenant1", ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void TenantEncryption_DifferentTenants_CannotDecrypt()
    {
        var masterKey = new byte[32];
        new Random(42).NextBytes(masterKey);
        var encryption = new TenantEncryption(masterKey);

        var plaintext = "Secret data"u8.ToArray();
        var ciphertext = encryption.Encrypt("tenant1", plaintext);

        // Attempting to decrypt with wrong tenant key should fail
        Assert.ThrowsAny<Exception>(() => encryption.Decrypt("tenant2", ciphertext));
    }

    [Fact]
    public void TenantEncryption_InvalidKeySize_Throws()
    {
        var invalidKey = new byte[16]; // Not 32 bytes
        Assert.Throws<ArgumentException>(() => new TenantEncryption(invalidKey));
    }
}
