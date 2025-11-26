using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SerialMemory.Core.Interfaces;
using SerialMemory.CoreExport.Models;
using SerialMemory.CoreExport.Services;
using SerialMemory.Infrastructure;
using SerialMemory.ML;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/core-export-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("CORE Export/Import Tool Starting");

    var builder = Host.CreateApplicationBuilder(args);

    // Get the directory where the executable is located (or project dir for dotnet run)
    var exeDir = AppContext.BaseDirectory;
    var appsettingsPath = Path.Combine(exeDir, "appsettings.json");

    Log.Information("Loading config from: {Path} (exists: {Exists})", appsettingsPath, File.Exists(appsettingsPath));

    // Add configuration from the executable directory
    builder.Configuration
        .SetBasePath(exeDir)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddEnvironmentVariables();

    // Configure services
    builder.Services.Configure<CoreConfig>(builder.Configuration.GetSection("Core"));

    // Add database configuration from environment or config
    var postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
    var postgresPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    var postgresUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    var postgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
    var postgresDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";

    var connectionString = $"Host={postgresHost};Port={postgresPort};Username={postgresUser};Password={postgresPassword};Database={postgresDb}";

    // Determine operation mode from command line
    var operation = args.Length > 0 ? args[0].ToLower() : "export";
    var needsImportServices = operation is "import" or "all";

    // Add SerialMemory services for import (only if needed)
    if (needsImportServices)
    {
        builder.Services.AddSingleton<IKnowledgeGraphStore>(sp =>
            new PostgresKnowledgeGraphStore(connectionString));

        // Add embedding service - Ollama
        var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
        var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "nomic-embed-text";
        var ollamaEmbeddingDim = int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_DIM"), out var dim) ? dim : 768;

        Log.Information("Using Ollama embedding service: {Model} at {Url} (dim={Dim})", ollamaModel, ollamaUrl, ollamaEmbeddingDim);
        builder.Services.AddSingleton<IEmbeddingService>(sp =>
            new OllamaEmbeddingService(ollamaUrl, ollamaModel, ollamaEmbeddingDim));

        // Add entity extraction
        builder.Services.AddSingleton<IEntityExtractionService, PatternEntityExtractionService>();

        // Add import service
        builder.Services.AddSingleton<SerialMemoryImporter>();
    }

    // Add HttpClient for CoreApiClient (always needed for export)
    builder.Services.AddHttpClient<CoreApiClient>();

    // Add export service (always add for export and all commands)
    builder.Services.AddSingleton<WorkspaceExporter>();

    // Add Serilog
    builder.Services.AddSerilog();

    var host = builder.Build();

    using var scope = host.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var cts = new CancellationTokenSource();

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;
        logger.LogWarning("Cancellation requested. Saving state and shutting down...");
        cts.Cancel();
    };

    try
    {
        switch (operation)
        {
            case "export":
                logger.LogInformation("Running CORE export");
                var exporter = scope.ServiceProvider.GetRequiredService<WorkspaceExporter>();
                await exporter.ExportAllWorkspaces(cts.Token);
                logger.LogInformation("Export completed successfully!");
                return 0;

            case "import":
                logger.LogInformation("Running SerialMemory import");
                var importer = scope.ServiceProvider.GetRequiredService<SerialMemoryImporter>();
                await importer.ImportFromExportDirectory(cts.Token);
                logger.LogInformation("Import completed successfully!");
                return 0;

            case "all":
                logger.LogInformation("Running export then import");

                var exp = scope.ServiceProvider.GetRequiredService<WorkspaceExporter>();
                await exp.ExportAllWorkspaces(cts.Token);
                logger.LogInformation("Export completed!");

                var imp = scope.ServiceProvider.GetRequiredService<SerialMemoryImporter>();
                await imp.ImportFromExportDirectory(cts.Token);
                logger.LogInformation("Import completed!");

                logger.LogInformation("Export and import completed successfully!");
                return 0;

            case "help":
            default:
                Console.WriteLine();
                Console.WriteLine("CORE Export/Import Tool");
                Console.WriteLine("=======================");
                Console.WriteLine();
                Console.WriteLine("Usage: SerialMemory.CoreExport <command>");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine("  export  - Export data from CORE API to local files");
                Console.WriteLine("  import  - Import exported CORE data into SerialMemory contextdb");
                Console.WriteLine("  all     - Export from CORE and then import to SerialMemory");
                Console.WriteLine("  help    - Show this help message");
                Console.WriteLine();
                Console.WriteLine("Configuration:");
                Console.WriteLine("  Edit appsettings.json to configure CORE API settings");
                Console.WriteLine("  Set environment variables for database connection:");
                Console.WriteLine("    POSTGRES_HOST, POSTGRES_PORT, POSTGRES_USER,");
                Console.WriteLine("    POSTGRES_PASSWORD, POSTGRES_DB");
                Console.WriteLine("  Set embedding service:");
                Console.WriteLine("    ONNX_MODEL_PATH + VOCAB_PATH (for ONNX)");
                Console.WriteLine("    or EMBEDDING_SERVICE_URL (for HTTP service)");
                Console.WriteLine();
                return operation == "help" ? 0 : 1;
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Operation cancelled by user. Progress has been saved and can be resumed.");
        return 1;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Operation failed with error");
        return 1;
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
