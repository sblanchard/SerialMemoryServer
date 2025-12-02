using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for API Keys management.
/// FIX #9: Cannot retrieve, copy to clipboard broken.
/// Fixed RLS, DTO mapping (snake_case to camelCase), and key_prefix return.
/// </summary>
public static class ApiKeysEndpoints
{
    public static void MapApiKeysEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api-keys")
            .WithTags("API Keys")
            .RequireAuthorization();

        // =============================================================================
        // GET /api-keys - List all API keys (FIX #9)
        // =============================================================================
        group.MapGet("", async (
            bool? includeRevoked,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IApiKeyService? apiKeyService,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL FIX: Set internal_admin role to bypass RLS for READ
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Use the SQL function that calculates status
            var keys = await conn.QueryAsync<ApiKeyListItemDto>(
                """
                SELECT
                    id,
                    name,
                    key_prefix AS keyPrefix,
                    scopes,
                    created_by AS createdBy,
                    last_used_at AS lastUsedAt,
                    expires_at AS expiresAt,
                    revoked_at AS revokedAt,
                    created_at AS createdAt,
                    CASE
                        WHEN revoked_at IS NOT NULL THEN 'revoked'
                        WHEN expires_at IS NOT NULL AND expires_at < NOW() THEN 'expired'
                        ELSE 'active'
                    END AS status
                FROM tenant_api_keys
                WHERE tenant_id = @TenantId
                    AND (@IncludeRevoked = TRUE OR revoked_at IS NULL)
                ORDER BY created_at DESC
                """,
                new { TenantId = tenantId, IncludeRevoked = includeRevoked ?? false });

            return Results.Ok(new
            {
                keys = keys.ToList(),
                total = keys.Count()
            });
        })
        .WithName("ListApiKeys")
        .WithDescription("Lists all API keys for the tenant")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /api-keys - Create a new API key (FIX #9)
        // =============================================================================
        group.MapPost("", async (
            [FromBody] CreateApiKeyApiRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IApiKeyService? apiKeyService,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "validation_error", message = "Key name is required" });

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL FIX: Set internal_admin role to bypass RLS for INSERT
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Check for duplicate name
            var existingKey = await conn.QueryFirstOrDefaultAsync<Guid?>(
                """
                SELECT id FROM tenant_api_keys
                WHERE tenant_id = @TenantId AND name = @Name AND revoked_at IS NULL
                """,
                new { TenantId = tenantId, Name = request.Name });

            if (existingKey.HasValue)
                return Results.Conflict(new { error = "duplicate", message = $"An API key named '{request.Name}' already exists" });

            // Generate new key
            var keyId = Guid.CreateVersion7();
            var (key, hash, prefix) = ApiKeyGenerator.Generate("live");
            var scopes = request.Scopes ?? new[] { "serialmemory.core" };

            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, expires_at, created_at)
                VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, @ExpiresAt, NOW())
                """,
                new
                {
                    Id = keyId,
                    TenantId = tenantId,
                    Name = request.Name,
                    KeyHash = hash,
                    KeyPrefix = prefix,
                    Scopes = scopes,
                    CreatedBy = userId,
                    ExpiresAt = request.ExpiresAt
                });

            // CRITICAL: Return the FULL key for clipboard copy - this is the only time it's shown
            return Results.Created($"/api-keys/{keyId}", new
            {
                id = keyId,
                name = request.Name,
                key = key,  // Full key for clipboard copy
                keyPrefix = prefix,
                scopes,
                expiresAt = request.ExpiresAt,
                createdAt = DateTimeOffset.UtcNow,
                message = "API key created successfully. Save this key - it will not be shown again."
            });
        })
        .WithName("CreateApiKey")
        .WithDescription("Creates a new API key")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status409Conflict);

        // =============================================================================
        // GET /api-keys/{keyId} - Get specific API key details
        // =============================================================================
        group.MapGet("/{keyId:guid}", async (
            Guid keyId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var key = await conn.QueryFirstOrDefaultAsync<ApiKeyDetailDto>(
                """
                SELECT
                    id,
                    name,
                    key_prefix AS keyPrefix,
                    scopes,
                    created_by AS createdBy,
                    last_used_at AS lastUsedAt,
                    expires_at AS expiresAt,
                    revoked_at AS revokedAt,
                    created_at AS createdAt,
                    CASE
                        WHEN revoked_at IS NOT NULL THEN 'revoked'
                        WHEN expires_at IS NOT NULL AND expires_at < NOW() THEN 'expired'
                        ELSE 'active'
                    END AS status
                FROM tenant_api_keys
                WHERE id = @KeyId AND tenant_id = @TenantId
                """,
                new { KeyId = keyId, TenantId = tenantId });

            if (key == null)
                return Results.NotFound(new { error = "not_found", message = "API key not found" });

            return Results.Ok(key);
        })
        .WithName("GetApiKey")
        .WithDescription("Gets details of a specific API key")
        .Produces<ApiKeyDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // DELETE /api-keys/{keyId} - Revoke an API key
        // =============================================================================
        group.MapDelete("/{keyId:guid}", async (
            Guid keyId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE tenant_api_keys
                SET revoked_at = NOW()
                WHERE id = @KeyId AND tenant_id = @TenantId AND revoked_at IS NULL
                """,
                new { KeyId = keyId, TenantId = tenantId });

            if (affected == 0)
                return Results.NotFound(new { error = "not_found", message = "API key not found or already revoked" });

            return Results.Ok(new { id = keyId, status = "revoked", message = "API key revoked successfully" });
        })
        .WithName("RevokeApiKey")
        .WithDescription("Revokes an API key")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /api-keys/{keyId}/regenerate - Regenerate (rotate) an API key
        // =============================================================================
        group.MapPost("/{keyId:guid}/regenerate", async (
            Guid keyId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get existing key details
            var existingKey = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT name, scopes, expires_at
                FROM tenant_api_keys
                WHERE id = @KeyId AND tenant_id = @TenantId AND revoked_at IS NULL
                """,
                new { KeyId = keyId, TenantId = tenantId });

            if (existingKey == null)
                return Results.NotFound(new { error = "not_found", message = "API key not found or already revoked" });

            // Revoke old key
            await conn.ExecuteAsync(
                "UPDATE tenant_api_keys SET revoked_at = NOW() WHERE id = @KeyId",
                new { KeyId = keyId });

            // Generate new key with same name and scopes
            var newKeyId = Guid.CreateVersion7();
            var (key, hash, prefix) = ApiKeyGenerator.Generate("live");

            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, expires_at, created_at)
                VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, @ExpiresAt, NOW())
                """,
                new
                {
                    Id = newKeyId,
                    TenantId = tenantId,
                    Name = (string)existingKey.name,
                    KeyHash = hash,
                    KeyPrefix = prefix,
                    Scopes = (string[])existingKey.scopes,
                    CreatedBy = userId,
                    ExpiresAt = existingKey.expires_at
                });

            return Results.Ok(new
            {
                oldKeyId = keyId,
                newKeyId,
                name = (string)existingKey.name,
                key = key,  // Full key for clipboard copy
                keyPrefix = prefix,
                message = "API key regenerated successfully. Save this key - it will not be shown again."
            });
        })
        .WithName("RegenerateApiKey")
        .WithDescription("Regenerates (rotates) an API key")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /api-keys/stats - Get API key usage stats
        // =============================================================================
        group.MapGet("/stats", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var stats = await conn.QueryFirstOrDefaultAsync<ApiKeyStatsDto>(
                """
                SELECT
                    COUNT(*) AS totalKeys,
                    COUNT(*) FILTER (WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > NOW())) AS activeKeys,
                    COUNT(*) FILTER (WHERE revoked_at IS NOT NULL) AS revokedKeys,
                    COUNT(*) FILTER (WHERE expires_at IS NOT NULL AND expires_at < NOW() AND revoked_at IS NULL) AS expiredKeys,
                    COUNT(*) FILTER (WHERE last_used_at > NOW() - INTERVAL '24 hours') AS usedLast24h,
                    MAX(last_used_at) AS lastUsedAt
                FROM tenant_api_keys
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            return Results.Ok(stats ?? new ApiKeyStatsDto());
        })
        .WithName("GetApiKeyStats")
        .WithDescription("Gets API key usage statistics")
        .Produces<ApiKeyStatsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return Guid.Parse("00000000-0000-0000-0000-000000000000");

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// DTOs for API Keys endpoints - CRITICAL: Using camelCase for frontend compatibility
public class ApiKeyListItemDto
{
    public Guid id { get; set; }
    public string name { get; set; } = "";
    public string keyPrefix { get; set; } = "";  // camelCase for frontend
    public string[]? scopes { get; set; }
    public string createdBy { get; set; } = "";  // camelCase for frontend
    public DateTimeOffset? lastUsedAt { get; set; }  // camelCase for frontend
    public DateTimeOffset? expiresAt { get; set; }  // camelCase for frontend
    public DateTimeOffset? revokedAt { get; set; }  // camelCase for frontend
    public DateTimeOffset createdAt { get; set; }  // camelCase for frontend
    public string status { get; set; } = "";
}

public sealed class ApiKeyDetailDto : ApiKeyListItemDto { }

public sealed class ApiKeyStatsDto
{
    public long totalKeys { get; set; }
    public long activeKeys { get; set; }
    public long revokedKeys { get; set; }
    public long expiredKeys { get; set; }
    public long usedLast24h { get; set; }
    public DateTimeOffset? lastUsedAt { get; set; }
}

public sealed record CreateApiKeyApiRequest(
    string Name,
    string[]? Scopes = null,
    DateTimeOffset? ExpiresAt = null
);
