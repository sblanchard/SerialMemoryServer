using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages;

public sealed class VerifyEmailModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VerifyEmailModel> _logger;

    public VerifyEmailModel(
        IHttpClientFactory httpClientFactory,
        ILogger<VerifyEmailModel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsSuccess { get; set; }
    public string Message { get; set; } = "";
    public string? ApiKey { get; set; }

    public async Task<IActionResult> OnGetAsync([FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            IsSuccess = false;
            Message = "Invalid verification link - no token provided.";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("DashboardApi");

            var response = await client.PostAsJsonAsync("/auth/verify-email", new { token });
            var result = await response.Content.ReadFromJsonAsync<VerifyResult>();

            if (response.IsSuccessStatusCode && result?.Success == true)
            {
                IsSuccess = true;
                Message = "Your email has been verified successfully!";
                ApiKey = result.ApiKey;
                _logger.LogInformation("Email verified successfully");
            }
            else
            {
                IsSuccess = false;
                Message = result?.Message ?? "Verification failed. The link may be expired or already used.";
                _logger.LogWarning("Email verification failed: {Message}", result?.Message);
            }
        }
        catch (Exception ex)
        {
            IsSuccess = false;
            Message = "An error occurred during verification. Please try again.";
            _logger.LogError(ex, "Email verification error");
        }

        return Page();
    }

    private sealed class VerifyResult
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public string? ApiKey { get; init; }
    }
}
