using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace SerialMemory.Tests.Billing;

[TestFixture]
[Category("BillingIntegration")]
public class BillingIntegrationTests
{
    private const string ApiBase = "https://api.serialmemory.dev";

    private readonly HttpClient _http = new();

    private string _tenantAKey = "sm_live_ZhF5DehVQEZxVY70kxdPsVSwihMhdbkm";
    private string _tenantBKey = "sm_live_4hW2bAqeWNg4VuirelUkLV9xMimRaV9z";

    // =====================================================================
    // SETUP — Create two clean tenants before each test suite
    // =====================================================================
    [SetUp]
    public async Task SetUp()
    {
        _tenantAKey = await RegisterTenantAsync($"test-a-{Guid.NewGuid():N}@example.com");
        _tenantBKey = await RegisterTenantAsync($"test-b-{Guid.NewGuid():N}@example.com");
    }

    // =====================================================================
    // Tenant Registration Helper
    // Matches your LIVE API's current SaaS onboarding model
    // =====================================================================
    private async Task<string> RegisterTenantAsync(string email)
    {
        var payload = new
        {
            email,
            password = "Test1234!",
            firstName = "Test",
            lastName = "User",
            workspaceName = "BillingTest",
            plan = "free"
        };

        var json = JsonSerializer.Serialize(payload);

        var resp = await _http.PostAsync(
            $"{ApiBase}/api/auth/register",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, $"Registration failed: {body}");

        var obj = JsonSerializer.Deserialize<RegisterResponse>(body);
        Assert.That(obj, Is.Not.Null);
        Assert.That(obj!.ApiKey, Is.Not.Null);

        return obj.ApiKey!;
    }

    // =====================================================================
    // 1. CHECKOUT SESSION
    // =====================================================================
    [Test]
    public async Task CreateCheckoutSession_Works()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAKey);

        var resp = await _http.PostAsync($"{ApiBase}/api/stripe/checkout", new StringContent("", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode, body);
        Assert.That(body.Contains("https://checkout.stripe.com"), "Expected Stripe Checkout URL");
    }

    // =====================================================================
    // 2. BILLING PORTAL
    // =====================================================================
    [Test]
    public async Task CreatePortalSession_Works()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAKey);

        var resp = await _http.PostAsync($"{ApiBase}/api/stripe/portal", new StringContent("", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode, body);
        Assert.That(body.Contains("https://billing.stripe.com"), "Expected Stripe Billing Portal URL");
    }

    // =====================================================================
    // 3. WEBHOOK PROCESSING
    // =====================================================================
    [Test]
    public async Task Webhook_ProcessesSubscriptionCreated()
    {
        var evt = BuildStripeWebhook("customer.subscription.created");

        var resp = await _http.PostAsync(
            $"{ApiBase}/api/stripe/webhook",
            new StringContent(evt.Json, Encoding.UTF8, "application/json")
        );

        Assert.That(resp.IsSuccessStatusCode, $"Webhook failed: {await resp.Content.ReadAsStringAsync()}");
    }

    [Test]
    public async Task Webhook_RejectsInvalidSignature()
    {
        var evt = BuildInvalidWebhook();

        var resp = await _http.PostAsync(
            $"{ApiBase}/api/stripe/webhook",
            new StringContent(evt.Json, Encoding.UTF8, "application/json")
        );

        Assert.That((int)resp.StatusCode == 400, "Invalid signature should produce HTTP 400");
    }

    // =====================================================================
    // 4. CREDITS ADDED AFTER SUBSCRIPTION
    // =====================================================================
    [Test]
    public async Task After_SubscriptionCreated_TenantGetsCredits()
    {
        // Simulate Stripe event
        var webhook = BuildStripeWebhook("customer.subscription.created");
        await _http.PostAsync($"{ApiBase}/api/stripe/webhook",
            new StringContent(webhook.Json, Encoding.UTF8, "application/json"));

        // Request billing status using tenant A’s key
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/billing/status");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode, body);

        var status = JsonSerializer.Deserialize<BillingStatusResponse>(body);

        Assert.That(status, Is.Not.Null, "Missing billing status");
        Assert.That(status!.Credits > 0, "Expected credits after subscription activation");
        Assert.That(status.Plan != "free", "Plan should not remain free after subscription");
    }

    // =====================================================================
    // 5. CANCEL SUBSCRIPTION → RETURN TO FREE
    // =====================================================================
    [Test]
    public async Task After_SubscriptionCancelled_TenantReturnsToFreePlan()
    {
        var webhook = BuildStripeWebhook("customer.subscription.deleted");

        await _http.PostAsync(
            $"{ApiBase}/api/stripe/webhook",
            new StringContent(webhook.Json, Encoding.UTF8, "application/json")
        );

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAKey);

        var resp = await _http.GetAsync($"{ApiBase}/api/billing/status");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.IsSuccessStatusCode, body);

        var status = JsonSerializer.Deserialize<BillingStatusResponse>(body);

        Assert.That(status!.Plan == "free", "Tenant should revert to free plan");
        Assert.That(status.Credits == 0, "Credits should be zero after cancellation");
    }

    // =====================================================================
    // 6. TENANT ISOLATION — ENSURE NO SHARED STRIPE CUSTOMER
    // =====================================================================
    [Test]
    public async Task TwoTenants_MustHaveSeparateStripeCustomers()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAKey);
        var aBody = await (await _http.GetAsync($"{ApiBase}/api/billing/status")).Content.ReadAsStringAsync();

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tenantBKey);
        var bBody = await (await _http.GetAsync($"{ApiBase}/api/billing/status")).Content.ReadAsStringAsync();

        var a = JsonSerializer.Deserialize<BillingStatusResponse>(aBody);
        var b = JsonSerializer.Deserialize<BillingStatusResponse>(bBody);

        Assert.That(a!.StripeCustomerId, Is.Not.EqualTo(b!.StripeCustomerId),
            "Two tenants share the SAME Stripe customer — critical isolation failure.");
    }

    // =====================================================================
    // WEBHOOK BUILDERS
    // =====================================================================
    private (string Json, string Signature) BuildStripeWebhook(string eventName)
    {
        var evt = new
        {
            id = $"evt_test_{Guid.NewGuid():N}",
            type = eventName,
            data = new { 
                @object = new { 
                    id = $"sub_{Guid.NewGuid():N}", 
                    status = eventName.Contains("created") ? "active" : "canceled"
                } 
            }
        };

        var json = JsonSerializer.Serialize(evt);
        return (json, "t=1234,v1=testsignature");
    }

    private (string Json, string Signature) BuildInvalidWebhook()
    {
        return ("{\"invalid\":true}", "bad-signature");
    }

    // =====================================================================
    // DTO CLASSES
    // =====================================================================
    private class RegisterResponse
    {
        public string? ApiKey { get; set; }
    }

    private class BillingStatusResponse
    {
        public string Plan { get; set; } = "free";
        public int Credits { get; set; }
        public string StripeCustomerId { get; set; } = "";
        public string StripeSubscriptionId { get; set; } = "";
    }
}
