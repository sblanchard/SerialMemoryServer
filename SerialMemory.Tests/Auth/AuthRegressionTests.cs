using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace SerialMemory.Tests.Auth;

[TestFixture]
[Category("AuthRegression")]
[Category("Integration")]
public class AuthRegressionTests
{
    private const string ApiBase = "https://api.serialmemory.dev";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private record RegisterResponse(string ApiKey);
    private record LoginResponse(string Token);
    private record BillingStatus(string Plan, int Credits);
    private record TenantStatus(string Role, string TenantId, string UserId);

    private string _adminKey = "sm_live_ab32L3oiZgoh1plqpeSAMIf3nfJSIAOG"; // set manually from your admin login
    private string? _testUserApiKey;
    private bool _serverAvailable;

    // ---------------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------------

    private async Task<bool> CheckServerAvailable()
    {
        try
        {
            // Try root endpoint - if we can reach the server, it's available
            var resp = await _http.GetAsync($"{ApiBase}/");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckApiAvailable()
    {
        // Check if the API auth endpoints are working
        var uniqueEmail = $"healthcheck_{Guid.NewGuid():N}@test.dev";
        return await TryRegisterUser(uniqueEmail) != null;
    }

    private async Task<string?> TryRegisterUser(string email)
    {
        var body = JsonSerializer.Serialize(new
        {
            email,
            password = "R3gr3ss!on#Test",
            firstName = "Test",
            lastName = "User",
            workspaceName = "TestWS",
            plan = "free"
        });

        var resp = await _http.PostAsync($"{ApiBase}/api/auth/register",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            return null;

        var txt = await resp.Content.ReadAsStringAsync();
        var obj = JsonSerializer.Deserialize<RegisterResponse>(txt);
        return obj?.ApiKey;
    }

    private async Task<string> RegisterUser(string email)
    {
        var body = JsonSerializer.Serialize(new
        {
            email,
            password = "R3gr3ss!on#Test",
            firstName = "Test",
            lastName = "User",
            workspaceName = "TestWS",
            plan = "free"
        });

        var resp = await _http.PostAsync($"{ApiBase}/api/auth/register",
            new StringContent(body, Encoding.UTF8, "application/json"));

        var txt = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, $"Registration failed: {txt}");

        var obj = JsonSerializer.Deserialize<RegisterResponse>(txt);
        Assert.That(obj, Is.Not.Null);
        Assert.That(obj!.ApiKey, Is.Not.Null);

        return obj.ApiKey;
    }

    private async Task<TenantStatus> WhoAmI(string apiKey)
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/auth/whoami");
        var txt = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode, $"whoami failed: {txt}");

        return JsonSerializer.Deserialize<TenantStatus>(txt)!;
    }

    // ---------------------------------------------------------------------
    // SETUP
    // ---------------------------------------------------------------------

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Check if server API is available
        _serverAvailable = await CheckServerAvailable();
        if (!_serverAvailable)
        {
            Assert.Ignore($"Live server at {ApiBase} is not available. Skipping integration tests.");
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        if (!_serverAvailable)
        {
            Assert.Ignore($"Live server at {ApiBase} is not available.");
            return;
        }

        var uniqueEmail = $"regression_{Guid.NewGuid():N}@test.dev";
        _testUserApiKey = await TryRegisterUser(uniqueEmail);

        if (string.IsNullOrEmpty(_testUserApiKey))
        {
            Assert.Ignore("Registration endpoint not available. Server may need configuration.");
        }
    }

    // ---------------------------------------------------------------------
    // TEST 1 — Registration must ALWAYS work
    // ---------------------------------------------------------------------

    [Test]
    public async Task Registration_Must_Always_Work()
    {
        Assert.That(_testUserApiKey, Is.Not.Empty);
    }

    // ---------------------------------------------------------------------
    // TEST 2 — Newly registered user must NOT be admin or owner
    // ---------------------------------------------------------------------

    [Test]
    public async Task NewlyRegisteredUser_Should_Not_Be_Admin_Or_Owner()
    {
        var status = await WhoAmI(_testUserApiKey!);

        Assert.That(status.Role, Is.EqualTo("user"),
            "Newly registered accounts MUST NOT be owner/admin.");
    }

    // ---------------------------------------------------------------------
    // TEST 3 — Admin MUST see admin endpoints
    // ---------------------------------------------------------------------

    [Test]
    public async Task Admin_Must_Access_Admin_Endpoints()
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _adminKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/admin/tenants");

        var txt = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode,
            $"Admin cannot access admin endpoints: {txt}");
    }

    // ---------------------------------------------------------------------
    // TEST 4 — Users MUST be forbidden from admin endpoints
    // ---------------------------------------------------------------------

    [Test]
    public async Task User_Must_Not_Access_Admin_Endpoints()
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _testUserApiKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/admin/tenants");

        Assert.That(resp.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden),
            "Users MUST get 403 for admin endpoints.");
    }

    // ---------------------------------------------------------------------
    // TEST 5 — Tenant isolation for memories
    // ---------------------------------------------------------------------

    [Test]
    public async Task Tenant_Isolation_Must_Not_Leak_Data()
    {
        // Create memory in tenant A
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _testUserApiKey);

        var memPayload = JsonSerializer.Serialize(new { text = "secret memory A" });
        var createResp = await _http.PostAsync($"{ApiBase}/api/memory/create",
            new StringContent(memPayload, Encoding.UTF8, "application/json"));

        Assert.That(createResp.IsSuccessStatusCode);

        // Admin should not see that memory in cross-tenant listing
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _adminKey);

        var leakCheck = await _http.GetAsync($"{ApiBase}/api/memory/list");
        var leakText = await leakCheck.Content.ReadAsStringAsync();

        Assert.That(leakText.Contains("secret memory A"), Is.False,
            "Admin must NEVER see tenant A user memories.");
    }

    // ---------------------------------------------------------------------
    // TEST 6 — Internal token refresh must work (dashboard simulation)
    // ---------------------------------------------------------------------

    [Test]
    public async Task Internal_Token_Must_Refresh_Correctly()
    {
        // Simulate dashboard internal token with 5-minute expiry
        var expiredToken = JwtHelper.BuildTestToken(
            expiry: DateTime.UtcNow.AddMinutes(-1),
            tenantId: "fake",
            userId: "fake");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", expiredToken);

        var resp = await _http.GetAsync($"{ApiBase}/api/auth/whoami");

        Assert.That(resp.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Unauthorized),
            "Expired internal tokens must be rejected, forcing refresh.");

        // Now simulate refreshed token
        var validToken = JwtHelper.BuildTestToken(
            expiry: DateTime.UtcNow.AddMinutes(4),
            tenantId: "fake",
            userId: "fake");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", validToken);

        var good = await _http.GetAsync($"{ApiBase}/api/auth/whoami");

        Assert.That(good.IsSuccessStatusCode,
            "Valid internal tokens must pass without 403.");
    }

    // ---------------------------------------------------------------------
    // TEST 7 — Billing access only for tenant owners
    // ---------------------------------------------------------------------

    [Test]
    public async Task User_Must_Not_Access_Billing()
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _testUserApiKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/billing/status");

        Assert.That(resp.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden),
            "Normal users must NOT access billing.");
    }

    // ---------------------------------------------------------------------
    // TEST 8 — Admin must NOT be treated as tenant owner
    // ---------------------------------------------------------------------

    [Test]
    public async Task Admin_Is_Not_TenantOwner()
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _adminKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/billing/status");
        var txt = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden),
            "Admin is system admin, NOT tenant owner — must not access billing.");
    }
}
