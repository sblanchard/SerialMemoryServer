using FluentAssertions;
using SerialMemory.Core.Models;
using Xunit;

namespace SerialMemory.Tests;

/// <summary>
/// Tests for API key generation and validation utilities.
/// Note: Integration tests with actual database are in separate test class.
/// </summary>
public class ApiKeyServiceTests
{
    [Fact]
    public void ApiKeyGenerator_Generate_ReturnsValidFormat()
    {
        // Act
        var (key, hash, prefix) = ApiKeyGenerator.Generate("live");

        // Assert
        key.Should().StartWith("sm_live_");
        key.Should().HaveLength(8 + 32); // "sm_live_" = 8 chars + 32 random
        prefix.Should().Be("sm_live_");
        hash.Should().NotBeNullOrEmpty();
        hash.Should().HaveLength(64); // SHA-256 = 64 hex chars
    }

    [Fact]
    public void ApiKeyGenerator_Generate_TestEnvironment()
    {
        // Act
        var (key, hash, prefix) = ApiKeyGenerator.Generate("test");

        // Assert
        key.Should().StartWith("sm_test_");
        prefix.Should().Be("sm_test_");
    }

    [Fact]
    public void ApiKeyGenerator_Generate_ProducesUniqueKeys()
    {
        // Act
        var keys = Enumerable.Range(0, 100)
            .Select(_ => ApiKeyGenerator.Generate())
            .ToList();

        // Assert
        var uniqueKeys = keys.Select(k => k.Key).Distinct().ToList();
        uniqueKeys.Should().HaveCount(100);

        var uniqueHashes = keys.Select(k => k.Hash).Distinct().ToList();
        uniqueHashes.Should().HaveCount(100);
    }

    [Fact]
    public void ApiKeyGenerator_ComputeHash_IsDeterministic()
    {
        // Arrange
        var key = "sm_live_abc123def456ghi789jkl012mno345";

        // Act
        var hash1 = ApiKeyGenerator.ComputeHash(key);
        var hash2 = ApiKeyGenerator.ComputeHash(key);

        // Assert
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ApiKeyGenerator_ComputeHash_IsCaseSensitive()
    {
        // Arrange
        var key1 = "sm_live_ABC123";
        var key2 = "sm_live_abc123";

        // Act
        var hash1 = ApiKeyGenerator.ComputeHash(key1);
        var hash2 = ApiKeyGenerator.ComputeHash(key2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ApiKeyGenerator_ExtractPrefix_WithValidKey()
    {
        // Arrange
        var key = "sm_live_abc123def456";

        // Act
        var prefix = ApiKeyGenerator.ExtractPrefix(key);

        // Assert
        prefix.Should().Be("sm_live_");
    }

    [Fact]
    public void ApiKeyGenerator_ExtractPrefix_WithTestKey()
    {
        // Arrange
        var key = "sm_test_xyz789";

        // Act
        var prefix = ApiKeyGenerator.ExtractPrefix(key);

        // Assert
        prefix.Should().Be("sm_test_");
    }

    [Fact]
    public void ApiKey_IsValid_WhenNotRevokedAndNotExpired()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Test Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
            ExpiresAt = null
        };

        // Assert
        apiKey.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ApiKey_IsValid_FalseWhenRevoked()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Revoked Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            RevokedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
        };

        // Assert
        apiKey.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ApiKey_IsValid_FalseWhenExpired()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Expired Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            RevokedAt = null,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) // Expired yesterday
        };

        // Assert
        apiKey.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ApiKey_IsValid_TrueWhenNotYetExpired()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Future Expiry Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) // Expires in 30 days
        };

        // Assert
        apiKey.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ApiKeyInfo_IsActive_MatchesApiKeyIsValid()
    {
        // Arrange - Active key
        var activeInfo = new ApiKeyInfo
        {
            Id = Guid.NewGuid(),
            Name = "Active",
            Prefix = "sm_live_",
            Scopes = ["read", "write"],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "user@test.com",
            RevokedAt = null,
            ExpiresAt = null
        };

        // Arrange - Revoked key
        var revokedInfo = new ApiKeyInfo
        {
            Id = Guid.NewGuid(),
            Name = "Revoked",
            Prefix = "sm_live_",
            Scopes = ["read"],
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedBy = "user@test.com",
            RevokedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
        };

        // Assert
        activeInfo.IsActive.Should().BeTrue();
        revokedInfo.IsActive.Should().BeFalse();
    }

    [Fact]
    public void SignupRequest_RequiredFields()
    {
        // Arrange & Act
        var request = new SignupRequest
        {
            TenantName = "Acme Corp",
            Email = "admin@acme.com",
            Slug = "acme",
            Plan = "starter"
        };

        // Assert
        request.TenantName.Should().Be("Acme Corp");
        request.Email.Should().Be("admin@acme.com");
        request.Slug.Should().Be("acme");
        request.Plan.Should().Be("starter");
    }

    [Fact]
    public void SignupRequest_OptionalFieldsCanBeNull()
    {
        // Arrange & Act
        var request = new SignupRequest
        {
            TenantName = "Test Company",
            Email = "test@company.com"
        };

        // Assert
        request.TenantName.Should().Be("Test Company");
        request.Email.Should().Be("test@company.com");
        request.Slug.Should().BeNull();
        request.Plan.Should().BeNull();
    }

    [Fact]
    public void TenantLimitsResult_WarningsSeverityLevels()
    {
        // Arrange
        var warnings = new List<LimitWarning>
        {
            new() { Type = "credits", Message = "75% used", PercentUsed = 75m, Severity = "info" },
            new() { Type = "credits", Message = "90% used", PercentUsed = 90m, Severity = "warning" },
            new() { Type = "rate_limit", Message = "Approaching limit", PercentUsed = 95m, Severity = "critical" }
        };

        var limits = new TenantLimitsResult
        {
            Plan = "starter",
            DisplayName = "Starter Plan",
            MonthlyCredits = 1000m,
            CreditsUsed = 900m,
            CreditsRemaining = 100m,
            DaysUntilReset = 10,
            CycleResetAt = DateTimeOffset.UtcNow.AddDays(10),
            RateLimitRpm = 60,
            RateLimitRpd = 1000,
            CurrentMemories = 500,
            CurrentEntities = 100,
            Warnings = warnings
        };

        // Assert
        limits.Warnings.Should().HaveCount(3);
        limits.Warnings.Should().Contain(w => w.Severity == "info");
        limits.Warnings.Should().Contain(w => w.Severity == "warning");
        limits.Warnings.Should().Contain(w => w.Severity == "critical");
    }

    [Fact]
    public void CreateApiKeyRequest_WithScopes()
    {
        // Arrange & Act
        var request = new CreateApiKeyRequest
        {
            Name = "Production Server",
            Scopes = ["read", "write", "admin"],
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Assert
        request.Name.Should().Be("Production Server");
        request.Scopes.Should().HaveCount(3);
        request.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddYears(1), TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// Key rotation scenario tests.
/// These test the workflow of rotating API keys (create new, revoke old).
/// </summary>
public class ApiKeyRotationTests
{
    [Fact]
    public void KeyRotation_OldKeyBecomesInvalid_AfterRevocation()
    {
        // Arrange - Simulate old key
        var oldKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Old Production Key",
            KeyHash = "oldhash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-365),
            RevokedAt = null,
            ExpiresAt = null
        };

        // Assert - Old key is valid before revocation
        oldKey.IsValid.Should().BeTrue();

        // Act - Simulate revocation (in real scenario, this would be via API)
        // Creating a new instance to simulate the state after revocation
        var revokedOldKey = new ApiKey
        {
            Id = oldKey.Id,
            TenantId = oldKey.TenantId,
            Name = oldKey.Name,
            KeyHash = oldKey.KeyHash,
            KeyPrefix = oldKey.KeyPrefix,
            CreatedBy = oldKey.CreatedBy,
            CreatedAt = oldKey.CreatedAt,
            RevokedAt = DateTimeOffset.UtcNow, // Now revoked
            ExpiresAt = null
        };

        // Assert - Old key is invalid after revocation
        revokedOldKey.IsValid.Should().BeFalse();
    }

    [Fact]
    public void KeyRotation_NewKeyIsValid_WhileOldKeyIsRevoked()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        var revokedOldKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Old Key (Revoked)",
            KeyHash = "oldhash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-365),
            RevokedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
        };

        var newKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "New Key",
            KeyHash = "newhash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
            ExpiresAt = null
        };

        // Assert
        revokedOldKey.IsValid.Should().BeFalse();
        newKey.IsValid.Should().BeTrue();
    }

    [Fact]
    public void KeyRotation_MultipleActiveKeys_BothValid()
    {
        // Arrange - Scenario where tenant has multiple active keys
        var tenantId = Guid.NewGuid();

        var productionKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Production Server",
            KeyHash = "prodhash",
            KeyPrefix = "sm_live_",
            CreatedBy = "admin@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            RevokedAt = null,
            ExpiresAt = null
        };

        var stagingKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Staging Server",
            KeyHash = "staginghash",
            KeyPrefix = "sm_test_",
            CreatedBy = "admin@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            RevokedAt = null,
            ExpiresAt = null
        };

        var ciKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "CI Pipeline",
            KeyHash = "cihash",
            KeyPrefix = "sm_test_",
            CreatedBy = "devops@test.com",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90) // Expires in 90 days
        };

        // Assert - All keys are valid
        productionKey.IsValid.Should().BeTrue();
        stagingKey.IsValid.Should().BeTrue();
        ciKey.IsValid.Should().BeTrue();
    }
}

/// <summary>
/// Tests for revoked key scenarios.
/// </summary>
public class RevokedKeyScenarioTests
{
    [Fact]
    public void RevokedKey_CannotBeUsed_EvenIfNotExpired()
    {
        // Arrange - Key with future expiry but revoked
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Revoked Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            RevokedAt = DateTimeOffset.UtcNow, // Just revoked
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1) // Not expired
        };

        // Assert
        key.IsValid.Should().BeFalse();
    }

    [Fact]
    public void RevokedKey_LastUsedAtPreserved()
    {
        // Arrange
        var lastUsed = DateTimeOffset.UtcNow.AddHours(-1);
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Previously Used Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            LastUsedAt = lastUsed,
            RevokedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
        };

        // Assert - Last used timestamp preserved even after revocation
        key.LastUsedAt.Should().Be(lastUsed);
        key.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ExpiredKey_CannotBeUsed()
    {
        // Arrange
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Expired Key",
            KeyHash = "hash",
            KeyPrefix = "sm_live_",
            CreatedBy = "user@test.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            RevokedAt = null, // Not revoked
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) // Expired yesterday
        };

        // Assert
        key.IsValid.Should().BeFalse();
    }

    [Fact]
    public void KeyInfo_ShowsRevokedStatus()
    {
        // Arrange
        var revokedAt = DateTimeOffset.UtcNow;
        var keyInfo = new ApiKeyInfo
        {
            Id = Guid.NewGuid(),
            Name = "Revoked Key",
            Prefix = "sm_live_",
            Scopes = ["read"],
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            CreatedBy = "user@test.com",
            RevokedAt = revokedAt,
            ExpiresAt = null
        };

        // Assert
        keyInfo.IsActive.Should().BeFalse();
        keyInfo.RevokedAt.Should().Be(revokedAt);
    }
}
