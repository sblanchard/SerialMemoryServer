using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using SerialMemory.Infrastructure.Email;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for billing and email service configuration.
/// </summary>
public static class BillingConfiguration
{
    /// <summary>
    /// Adds Stripe billing services if configured.
    /// </summary>
    public static IServiceCollection AddStripeBilling(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var stripeConfig = GetStripeConfig(configuration);

        if (stripeConfig != null)
        {
            services.AddSingleton(stripeConfig);
            services.AddSingleton<IBillingService>(sp =>
                new StripeBillingService(
                    connectionString,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<StripeBillingService>(),
                    stripeConfig));
            Console.WriteLine("[INFO] Stripe billing service configured");
        }
        else
        {
            Console.WriteLine("[WARN] Stripe not configured - billing features disabled");
        }

        return services;
    }

    /// <summary>
    /// Adds email service (Azure Communication Services or NoOp fallback).
    /// </summary>
    public static IServiceCollection AddEmailService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var acsConnectionString = configuration["Email:AcsConnectionString"]
            ?? configuration["AzureCommunicationServices:ConnectionString"]
            ?? configuration["AzureCommunicationServices__ConnectionString"]
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_ACS_CONNECTION");

        if (!string.IsNullOrEmpty(acsConnectionString))
        {
            services.AddSingleton<IEmailService, AcsEmailService>();
        }
        else
        {
            Console.WriteLine("[WARN] ACS not configured - using NoOp email service (emails will be logged but not sent)");
            services.AddSingleton<IEmailService, NoOpEmailService>();
        }

        return services;
    }

    /// <summary>
    /// Gets Stripe configuration from environment if available.
    /// </summary>
    private static StripeConfig? GetStripeConfig(IConfiguration configuration)
    {
        var secretKey = configuration["Stripe:SecretKey"]
            ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
        var webhookSecret = configuration["Stripe:WebhookSecret"]
            ?? Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");

        if (string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(webhookSecret))
        {
            return null;
        }

        var publishableKey = configuration["Stripe:PublishableKey"]
            ?? Environment.GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY");
        var proPriceId = configuration["Stripe:ProPriceId"]
            ?? Environment.GetEnvironmentVariable("STRIPE_PRO_PRICE_ID");
        var proPlusPriceId = configuration["Stripe:ProPlusPriceId"]
            ?? Environment.GetEnvironmentVariable("STRIPE_PRO_PLUS_PRICE_ID");
        var teamPriceId = configuration["Stripe:TeamPriceId"]
            ?? Environment.GetEnvironmentVariable("STRIPE_TEAM_PRICE_ID");
        var enterprisePriceId = configuration["Stripe:EnterprisePriceId"]
            ?? Environment.GetEnvironmentVariable("STRIPE_ENTERPRISE_PRICE_ID");

        var baseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_BASE_URL")
            ?? "https://serialmemory.dev";

        return new StripeConfig
        {
            SecretKey = secretKey,
            PublishableKey = publishableKey ?? "",
            WebhookSecret = webhookSecret,
            ProPriceId = proPriceId ?? "",
            ProPlusPriceId = proPlusPriceId,
            TeamPriceId = teamPriceId,
            EnterprisePriceId = enterprisePriceId ?? "",
            DefaultSuccessUrl = $"{baseUrl}/dashboard/billing?success=true",
            DefaultCancelUrl = $"{baseUrl}/dashboard/billing?canceled=true"
        };
    }
}
