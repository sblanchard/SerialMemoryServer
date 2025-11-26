using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var apiBaseUrl = builder.Configuration["API_BASE_URL"] ?? "http://localhost:5000";
var stripePublishableKey = builder.Configuration["STRIPE_PUBLISHABLE_KEY"] ?? "";

// Add services
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("Api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

// Store configuration for views
builder.Services.AddSingleton(new AppConfig
{
    ApiBaseUrl = apiBaseUrl,
    StripePublishableKey = stripePublishableKey
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

public sealed class AppConfig
{
    public string ApiBaseUrl { get; init; } = "";
    public string StripePublishableKey { get; init; } = "";
}
