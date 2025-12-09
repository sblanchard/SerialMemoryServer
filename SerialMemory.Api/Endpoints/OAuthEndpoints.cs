using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Endpoints;

/// <summary>
/// OAuth 2.0 endpoints for ChatGPT and other OAuth clients.
/// Implements authorization code flow with PKCE and client credentials grant.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/oauth")
            .WithTags("OAuth")
            .AllowAnonymous();

        // GET /oauth/authorize - Authorization endpoint (user-facing)
        group.MapGet("/authorize", HandleAuthorize)
            .WithName("OAuthAuthorize")
            .WithDescription("OAuth 2.0 authorization endpoint");

        // POST /oauth/authorize - Process authorization (after user consent)
        group.MapPost("/authorize", HandleAuthorizePost)
            .WithName("OAuthAuthorizePost")
            .WithDescription("Process OAuth authorization");

        // POST /oauth/token - Token endpoint
        group.MapPost("/token", HandleToken)
            .WithName("OAuthToken")
            .WithDescription("OAuth 2.0 token endpoint")
            .Accepts<OAuthTokenRequest>("application/x-www-form-urlencoded");

        // POST /oauth/revoke - Revoke token
        group.MapPost("/revoke", HandleRevoke)
            .WithName("OAuthRevoke")
            .WithDescription("OAuth 2.0 token revocation endpoint");

        // GET /.well-known/oauth-authorization-server - OAuth metadata
        app.MapGet("/.well-known/oauth-authorization-server", HandleMetadata)
            .WithName("OAuthMetadata")
            .WithDescription("OAuth 2.0 authorization server metadata")
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleAuthorize(
        HttpContext context,
        [FromQuery] string response_type,
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string? scope,
        [FromQuery] string? state,
        [FromQuery] string? code_challenge,
        [FromQuery] string? code_challenge_method,
        [FromServices] OAuthService oauthService)
    {
        // Validate response_type
        if (response_type != "code")
        {
            return Results.BadRequest(new { error = "unsupported_response_type" });
        }

        // For ChatGPT integration, we auto-approve since the client is pre-authenticated
        // In a full implementation, you'd show a consent screen here

        // Validate client exists
        // Note: For authorize flow, we don't have the client_secret yet, just validate client_id exists
        var requestedScopes = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? ["serialmemory.core"];

        // Return a simple authorization page
        var html = GenerateAuthorizePage(client_id, redirect_uri, state, code_challenge, code_challenge_method, requestedScopes);
        return Results.Content(html, "text/html");
    }

    private static async Task<IResult> HandleAuthorizePost(
        HttpContext context,
        [FromServices] OAuthService oauthService,
        [FromServices] IConfiguration configuration)
    {
        var form = await context.Request.ReadFormAsync();

        var clientId = form["client_id"].ToString();
        var clientSecret = form["client_secret"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var state = form["state"].ToString();
        var codeChallenge = form["code_challenge"].ToString();
        var codeChallengeMethod = form["code_challenge_method"].ToString();
        var scopeStr = form["scope"].ToString();
        var action = form["action"].ToString();

        if (action == "deny")
        {
            var denyUri = $"{redirectUri}?error=access_denied&error_description=User+denied+the+request";
            if (!string.IsNullOrEmpty(state))
                denyUri += $"&state={HttpUtility.UrlEncode(state)}";
            return Results.Redirect(denyUri);
        }

        // Validate client credentials
        var client = await oauthService.ValidateClientAsync(clientId, clientSecret, context.RequestAborted);
        if (client == null)
        {
            return Results.BadRequest(new { error = "invalid_client" });
        }

        // Validate redirect URI - allow exact matches or trusted OAuth callback domains
        var isValidRedirect = client.RedirectUris.Contains(redirectUri) ||
            IsTrustedOAuthRedirect(redirectUri);

        if (!isValidRedirect)
        {
            return Results.BadRequest(new { error = "invalid_redirect_uri" });
        }

        var scopes = scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Create authorization code
        var code = await oauthService.CreateAuthorizationCodeAsync(
            client,
            redirectUri,
            scopes,
            state,
            string.IsNullOrEmpty(codeChallenge) ? null : codeChallenge,
            string.IsNullOrEmpty(codeChallengeMethod) ? null : codeChallengeMethod,
            context.RequestAborted);

        // Redirect with code
        var redirectUrl = $"{redirectUri}?code={HttpUtility.UrlEncode(code)}";
        if (!string.IsNullOrEmpty(state))
            redirectUrl += $"&state={HttpUtility.UrlEncode(state)}";

        return Results.Redirect(redirectUrl);
    }

    private static async Task<IResult> HandleToken(
        HttpContext context,
        [FromServices] OAuthService oauthService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("OAuth");

        // Parse form data
        var form = await context.Request.ReadFormAsync();

        var grantType = form["grant_type"].ToString();
        var clientId = form["client_id"].ToString();
        var clientSecret = form["client_secret"].ToString();

        logger.LogInformation("OAuth token request: grant_type={GrantType}, client_id={ClientId}",
            grantType, clientId);

        // Also check Authorization header for client credentials
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var credentials = Encoding.UTF8.GetString(
                    Convert.FromBase64String(authHeader["Basic ".Length..]));
                var parts = credentials.Split(':', 2);
                if (parts.Length == 2)
                {
                    clientId = HttpUtility.UrlDecode(parts[0]);
                    clientSecret = HttpUtility.UrlDecode(parts[1]);
                }
            }
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return Results.Json(new { error = "invalid_client" }, statusCode: 401);
        }

        OAuthTokenResponse? response = grantType switch
        {
            "authorization_code" => await HandleAuthorizationCodeGrant(form, clientId, clientSecret, oauthService, context.RequestAborted),
            "client_credentials" => await HandleClientCredentialsGrant(form, clientId, clientSecret, oauthService, context.RequestAborted),
            "refresh_token" => await HandleRefreshTokenGrant(form, clientId, clientSecret, oauthService, context.RequestAborted),
            _ => null
        };

        if (response == null)
        {
            logger.LogWarning("OAuth token request failed: grant_type={GrantType}, client_id={ClientId}",
                grantType, clientId);
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        logger.LogInformation("OAuth token issued for client_id={ClientId}", clientId);

        return Results.Json(new
        {
            access_token = response.AccessToken,
            token_type = response.TokenType,
            expires_in = response.ExpiresIn,
            refresh_token = response.RefreshToken,
            scope = response.Scope
        });
    }

    private static async Task<OAuthTokenResponse?> HandleAuthorizationCodeGrant(
        IFormCollection form,
        string clientId,
        string clientSecret,
        OAuthService oauthService,
        CancellationToken ct)
    {
        var code = form["code"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri))
            return null;

        return await oauthService.ExchangeCodeAsync(
            code, clientId, clientSecret, redirectUri,
            string.IsNullOrEmpty(codeVerifier) ? null : codeVerifier, ct);
    }

    private static async Task<OAuthTokenResponse?> HandleClientCredentialsGrant(
        IFormCollection form,
        string clientId,
        string clientSecret,
        OAuthService oauthService,
        CancellationToken ct)
    {
        var scopeStr = form["scope"].ToString();
        var scopes = string.IsNullOrEmpty(scopeStr)
            ? null
            : scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return await oauthService.ClientCredentialsGrantAsync(clientId, clientSecret, scopes, ct);
    }

    private static async Task<OAuthTokenResponse?> HandleRefreshTokenGrant(
        IFormCollection form,
        string clientId,
        string clientSecret,
        OAuthService oauthService,
        CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].ToString();

        if (string.IsNullOrEmpty(refreshToken))
            return null;

        return await oauthService.RefreshTokenAsync(refreshToken, clientId, clientSecret, ct);
    }

    private static async Task<IResult> HandleRevoke(
        HttpContext context,
        [FromServices] OAuthService oauthService)
    {
        var form = await context.Request.ReadFormAsync();
        var token = form["token"].ToString();

        if (!string.IsNullOrEmpty(token))
        {
            await oauthService.RevokeRefreshTokenAsync(token, context.RequestAborted);
        }

        return Results.Ok();
    }

    private static IResult HandleMetadata(HttpRequest request)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";

        return Results.Json(new
        {
            issuer = baseUrl,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            revocation_endpoint = $"{baseUrl}/oauth/revoke",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
            code_challenge_methods_supported = new[] { "S256", "plain" },
            scopes_supported = new[] { "serialmemory.core", "serialmemory.read", "serialmemory.write" }
        });
    }

    /// <summary>
    /// Checks if redirect URI is from a trusted OAuth provider (ChatGPT, etc.)
    /// </summary>
    private static bool IsTrustedOAuthRedirect(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;

        // Trusted domains for OAuth callbacks
        var trustedHosts = new[]
        {
            "chat.openai.com",
            "chatgpt.com",
            "platform.openai.com",
            "api.openai.com"
        };

        return trustedHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateAuthorizePage(
        string clientId,
        string redirectUri,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        string[] scopes)
    {
        var scopesList = string.Join("", scopes.Select(s => $"<li>{HttpUtility.HtmlEncode(s)}</li>"));
        var encodedClientId = HttpUtility.HtmlEncode(clientId);
        var attrClientId = HttpUtility.HtmlAttributeEncode(clientId);
        var attrRedirectUri = HttpUtility.HtmlAttributeEncode(redirectUri);
        var attrState = HttpUtility.HtmlAttributeEncode(state ?? "");
        var attrCodeChallenge = HttpUtility.HtmlAttributeEncode(codeChallenge ?? "");
        var attrCodeChallengeMethod = HttpUtility.HtmlAttributeEncode(codeChallengeMethod ?? "");
        var attrScope = HttpUtility.HtmlAttributeEncode(string.Join(" ", scopes));

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <title>Authorize Application</title>
                <style>
                    body { font-family: system-ui, sans-serif; max-width: 500px; margin: 50px auto; padding: 20px; }
                    .card { background: #f8f9fa; border-radius: 8px; padding: 24px; }
                    h1 { color: #333; font-size: 24px; }
                    .scopes { background: #e9ecef; padding: 12px; border-radius: 4px; margin: 16px 0; }
                    .buttons { display: flex; gap: 12px; margin-top: 20px; }
                    button { padding: 12px 24px; border-radius: 6px; border: none; cursor: pointer; font-size: 16px; }
                    .approve { background: #0d6efd; color: white; }
                    .deny { background: #6c757d; color: white; }
                    input[type=password] { width: 100%; padding: 8px; margin: 8px 0; border: 1px solid #ccc; border-radius: 4px; }
                    label { font-weight: 500; }
                </style>
            </head>
            <body>
                <div class="card">
                    <h1>Authorize Application</h1>
                    <p>An application is requesting access to your SerialMemory account.</p>
                    <p><strong>Client ID:</strong> {{encodedClientId}}</p>
                    <div class="scopes">
                        <strong>Requested permissions:</strong>
                        <ul>
                            {{scopesList}}
                        </ul>
                    </div>
                    <form method="post" action="/oauth/authorize">
                        <input type="hidden" name="client_id" value="{{attrClientId}}" />
                        <input type="hidden" name="redirect_uri" value="{{attrRedirectUri}}" />
                        <input type="hidden" name="state" value="{{attrState}}" />
                        <input type="hidden" name="code_challenge" value="{{attrCodeChallenge}}" />
                        <input type="hidden" name="code_challenge_method" value="{{attrCodeChallengeMethod}}" />
                        <input type="hidden" name="scope" value="{{attrScope}}" />

                        <label>Client Secret:</label>
                        <input type="password" name="client_secret" required placeholder="Enter your client secret" />

                        <div class="buttons">
                            <button type="submit" name="action" value="approve" class="approve">Authorize</button>
                            <button type="submit" name="action" value="deny" class="deny">Deny</button>
                        </div>
                    </form>
                </div>
            </body>
            </html>
            """;
    }
}

// Request DTOs
public sealed record OAuthTokenRequest(
    string grant_type,
    string? client_id,
    string? client_secret,
    string? code,
    string? redirect_uri,
    string? refresh_token,
    string? scope,
    string? code_verifier
);
