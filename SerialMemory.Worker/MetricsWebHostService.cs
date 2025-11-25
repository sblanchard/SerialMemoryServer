using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace SerialMemory.Worker;

internal class MetricsWebHostService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var webBuilder = WebApplication.CreateBuilder();
        webBuilder.WebHost.UseUrls("http://0.0.0.0:8081");
        webBuilder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("SerialMemory.Worker"))
            .WithMetrics(mb => mb.AddPrometheusExporter());

        var app = webBuilder.Build();
        app.MapPrometheusScrapingEndpoint();

        await app.RunAsync(stoppingToken);
    }
}