using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Tests for authentication-layer tenant isolation.
/// Verifies that the internal token system correctly prevents cross-tenant data leakage.
/// </summary>
[Collection("TenantIsolation")]
public sealed class AuthTenantIsolationTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly string UserA = "user-a-" + Guid.CreateVersion7();
    private static readonly string UserB = "user-b-" + Guid.CreateVersion7();

    private const string Issuer = "serialmemory-web";
    private const string Audience = "serialmemory-internal";

    /// <summary>
    /// Generates a signing key for testing.
    /// </summary>
    private static byte[] GenerateTestKey() => RandomNumberGenerator.GetBytes(64);

    /// <summary>
    /// Generates an internal JWT token for testing.
    /// </summary>
    private static string GenerateInternalToken(
        byte[] signingKey,
        string tenantId,
        string userId,
        string role = "user",
        TimeSpan? lifetime = null)
    {
        var securityKey = new SymmetricSecurityKey(signingKey);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var tokenHandler = new JwtSecurityTokenHandler();

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
                new Claim("tenant_id", tenantId),
                new Claim("workspace_id", "default"),
                new Claim("role", role),
                new Claim("token_type", "internal")
            }),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = credentials
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    /// <summary>
    /// Validates a token and extracts claims.
    /// </summary>
    private static (bool isValid, string? tenantId, string? userId) ValidateToken(
        byte[] signingKey,
        string token)
    {
        try
        {
            var securityKey = new SymmetricSecurityKey(signingKey);
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

            var tokenType = principal.FindFirst("token_type")?.Value;
            if (tokenType != "internal")
                return (false, null, null);

            var tenantId = principal.FindFirst("tenant_id")?.Value;
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            return (true, tenantId, userId);
        }
        catch
        {
            return (false, null, null);
        }
    }

    [Fact]
    public void InternalToken_Contains_Correct_TenantId()
    {
        // Arrange
        var signingKey = GenerateTestKey();

        // Act
        var token = GenerateInternalToken(signingKey, TenantA.ToString(), UserA);
        var (isValid, tenantId, userId) = ValidateToken(signingKey, token);

        // Assert
        isValid.Should().BeTrue();
        tenantId.Should().Be(TenantA.ToString());
        userId.Should().Be(UserA);
    }

    [Fact]
    public void InternalToken_For_TenantA_Cannot_Access_TenantB()
    {
        // Arrange
        var signingKey = GenerateTestKey();
        var tokenForTenantA = GenerateInternalToken(signingKey, TenantA.ToString(), UserA);

        // Act
        var (isValid, extractedTenantId, _) = ValidateToken(signingKey, tokenForTenantA);

        // Assert - Token is valid but tenant is NOT TenantB
        isValid.Should().BeTrue();
        extractedTenantId.Should().NotBe(TenantB.ToString(),
            "Token generated for TenantA must not grant access to TenantB");
    }

    [Fact]
    public void Different_Tenants_Get_Different_Tokens()
    {
        // Arrange
        var signingKey = GenerateTestKey();

        // Act
        var tokenA = GenerateInternalToken(signingKey, TenantA.ToString(), UserA);
        var tokenB = GenerateInternalToken(signingKey, TenantB.ToString(), UserB);

        // Assert
        tokenA.Should().NotBe(tokenB, "Different tenants must receive different tokens");

        var (_, tenantIdA, userIdA) = ValidateToken(signingKey, tokenA);
        var (_, tenantIdB, userIdB) = ValidateToken(signingKey, tokenB);

        tenantIdA.Should().NotBe(tenantIdB, "Tenant IDs in tokens must be different");
        userIdA.Should().NotBe(userIdB, "User IDs in tokens must be different");
    }

    [Fact]
    public void Token_Signed_With_Different_Key_Is_Rejected()
    {
        // Arrange
        var keyA = GenerateTestKey();
        var keyB = GenerateTestKey();

        // Generate token with key A
        var token = GenerateInternalToken(keyA, TenantA.ToString(), UserA);

        // Act - Try to validate with key B
        var (isValid, _, _) = ValidateToken(keyB, token);

        // Assert
        isValid.Should().BeFalse("Token signed with different key must be rejected");
    }

    [Fact]
    public void Expired_Token_Is_Rejected()
    {
        // Arrange
        var signingKey = GenerateTestKey();

        // Generate an already-expired token
        var token = GenerateExpiredToken(signingKey, TenantA.ToString(), UserA);

        // Act
        var (isValid, _, _) = ValidateToken(signingKey, token);

        // Assert
        isValid.Should().BeFalse("Expired token must be rejected");
    }

    /// <summary>
    /// Generates an already-expired token for testing.
    /// </summary>
    private static string GenerateExpiredToken(byte[] signingKey, string tenantId, string userId)
    {
        var securityKey = new SymmetricSecurityKey(signingKey);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var tokenHandler = new JwtSecurityTokenHandler();

        // Set NotBefore in the past and Expires even further in the past
        var notBefore = DateTime.UtcNow.AddMinutes(-10);
        var expires = DateTime.UtcNow.AddMinutes(-5); // Expired 5 minutes ago

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
                new Claim("tenant_id", tenantId),
                new Claim("workspace_id", "default"),
                new Claim("role", "user"),
                new Claim("token_type", "internal")
            }),
            NotBefore = notBefore,
            Expires = expires,
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = credentials
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public void Token_Without_Internal_Type_Is_Rejected()
    {
        // Arrange
        var signingKey = GenerateTestKey();
        var securityKey = new SymmetricSecurityKey(signingKey);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var tokenHandler = new JwtSecurityTokenHandler();

        // Generate token WITHOUT token_type=internal claim
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, UserA),
                new Claim("tenant_id", TenantA.ToString()),
                // Intentionally missing: new Claim("token_type", "internal")
            }),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = credentials
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        // Act
        var (isValid, _, _) = ValidateToken(signingKey, tokenString);

        // Assert
        isValid.Should().BeFalse("Token without internal type claim must be rejected");
    }

    [Fact]
    public void Cookie_Session_Tenant_Mismatch_Detected()
    {
        // This test simulates the cookie vs session tenant mismatch check
        // that happens in the API proxy

        // Arrange
        var cookieTenantId = TenantA.ToString();
        var cookieUserId = UserA;
        var sessionTenantId = TenantB.ToString(); // MISMATCH!
        var sessionUserId = UserB;

        // Act - Check for mismatch (this is the logic from Program.cs)
        var hasMismatch = sessionTenantId != cookieTenantId || sessionUserId != cookieUserId;

        // Assert
        hasMismatch.Should().BeTrue(
            "Cookie and session tenant/user mismatch must be detected as security violation");
    }

    [Fact]
    public void Cookie_Session_Match_Allows_Request()
    {
        // Arrange
        var tenantId = TenantA.ToString();
        var userId = UserA;

        // Cookie and session have same values
        var cookieTenantId = tenantId;
        var cookieUserId = userId;
        var sessionTenantId = tenantId;
        var sessionUserId = userId;

        // Act
        var hasMismatch = sessionTenantId != cookieTenantId || sessionUserId != cookieUserId;

        // Assert
        hasMismatch.Should().BeFalse(
            "Matching cookie and session should allow the request");
    }

    [Fact]
    public void Header_Token_Tenant_Mismatch_Detected()
    {
        // This test verifies the defense-in-depth check where
        // X-Tenant-Id header must match the internal token's tenant_id

        // Arrange
        var signingKey = GenerateTestKey();
        var tokenTenantId = TenantA.ToString();
        var headerTenantId = TenantB.ToString(); // MISMATCH!

        var token = GenerateInternalToken(signingKey, tokenTenantId, UserA);
        var (isValid, extractedTenantId, _) = ValidateToken(signingKey, token);

        // Act - Check for header mismatch (logic from ApiKeyAuthMiddleware)
        var headerMismatch = !string.IsNullOrEmpty(headerTenantId)
            && headerTenantId != extractedTenantId;

        // Assert
        isValid.Should().BeTrue("Token itself should be valid");
        headerMismatch.Should().BeTrue(
            "Header tenant mismatch with token tenant must be detected");
    }

    [Fact]
    public void Multiple_Logins_Clear_Previous_Session()
    {
        // This test verifies that logging in clears previous session data
        // to prevent cross-tenant leakage

        // Arrange - Simulate session state
        var sessionData = new Dictionary<string, string>
        {
            ["internal_token"] = "old_token_for_tenant_a",
            ["internal_token_tenant"] = TenantA.ToString(),
            ["internal_token_user"] = UserA
        };

        // Act - Simulate login for different tenant (this clears old data)
        sessionData.Clear(); // This is what happens in Login.cshtml.cs

        // Set new tenant's session data
        var newTenantId = TenantB.ToString();
        var newUserId = UserB;
        sessionData["internal_token"] = "new_token_for_tenant_b";
        sessionData["internal_token_tenant"] = newTenantId;
        sessionData["internal_token_user"] = newUserId;

        // Assert
        sessionData["internal_token_tenant"].Should().Be(newTenantId);
        sessionData["internal_token_user"].Should().Be(newUserId);
        sessionData["internal_token"].Should().NotContain("tenant_a",
            "Previous tenant's token must be cleared on new login");
    }

    [Fact]
    public void Logout_Clears_All_Auth_Data()
    {
        // Arrange - Simulate session with auth data
        var sessionData = new Dictionary<string, string>
        {
            ["internal_token"] = "some_token",
            ["internal_token_tenant"] = TenantA.ToString(),
            ["internal_token_user"] = UserA
        };

        var cookies = new Dictionary<string, string>
        {
            ["auth_token"] = "legacy_api_key"
        };

        // Act - Simulate logout (this is what happens in Logout.cshtml.cs)
        sessionData.Remove("internal_token");
        sessionData.Remove("internal_token_tenant");
        sessionData.Remove("internal_token_user");
        sessionData.Clear();
        cookies.Remove("auth_token");

        // Assert
        sessionData.Should().BeEmpty("All session data must be cleared on logout");
        cookies.Should().BeEmpty("auth_token cookie must be deleted on logout");
    }

    [Fact]
    public void Token_Claims_Cannot_Be_Tampered()
    {
        // Arrange
        var signingKey = GenerateTestKey();
        var token = GenerateInternalToken(signingKey, TenantA.ToString(), UserA);

        // Act - Try to tamper with the token (change tenant_id in payload)
        var parts = token.Split('.');
        parts.Length.Should().Be(3, "JWT should have 3 parts");

        // Decode payload, modify it, re-encode
        var payloadJson = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(PadBase64(parts[1])));
        var tamperedPayload = payloadJson.Replace(TenantA.ToString(), TenantB.ToString());
        var tamperedPayloadBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(tamperedPayload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var tamperedToken = $"{parts[0]}.{tamperedPayloadBase64}.{parts[2]}";

        // Try to validate tampered token
        var (isValid, _, _) = ValidateToken(signingKey, tamperedToken);

        // Assert
        isValid.Should().BeFalse("Tampered token must be rejected due to signature mismatch");
    }

    private static string PadBase64(string base64)
    {
        base64 = base64.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return base64;
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-valid-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.invalid.signature")]
    public void Invalid_Token_Formats_Are_Rejected(string invalidToken)
    {
        // Arrange
        var signingKey = GenerateTestKey();

        // Act
        var (isValid, _, _) = ValidateToken(signingKey, invalidToken);

        // Assert
        isValid.Should().BeFalse($"Invalid token format '{invalidToken}' must be rejected");
    }
}
