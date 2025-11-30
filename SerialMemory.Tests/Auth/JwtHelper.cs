using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;

public static class JwtHelper
{
    private const string TestKey = "THIS_IS_A_TEST_KEY_CHANGE_ME_1234567890";

    public static string BuildTestToken(DateTime expiry, string tenantId, string userId)
    {
        var handler = new JwtSecurityTokenHandler();

        var descriptor = new SecurityTokenDescriptor
        {
            Expires = expiry,
            Claims = new Dictionary<string, object>
            {
                { "tid", tenantId },
                { "sub", userId },
                { "role", "user" }
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)),
                SecurityAlgorithms.HmacSha256Signature)
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}