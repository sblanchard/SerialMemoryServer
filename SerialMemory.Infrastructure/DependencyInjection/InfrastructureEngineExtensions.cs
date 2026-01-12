using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Operations;
using SerialMemory.Infrastructure.Compilation;
using SerialMemory.Infrastructure.ContextOptimization;
using SerialMemory.Infrastructure.Debugging;
using SerialMemory.Infrastructure.DeterministicInference;
using SerialMemory.Infrastructure.Reasoning;
using SerialMemory.Infrastructure.Security;
using SerialMemory.Infrastructure.Services;

namespace SerialMemory.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration for optional infrastructure engines.
/// All engines are registered with feature flag guards.
/// </summary>
public static class InfrastructureEngineExtensions
{
    /// <summary>
    /// Registers all optional infrastructure engines with feature flag guards.
    /// Engines are only activated when their feature flag is enabled.
    /// </summary>
    public static IServiceCollection AddInfrastructureEngines(
        this IServiceCollection services,
        SystemFeatureFlags? flags = null)
    {
        flags ??= SystemFeatureFlags.FromEnvironment();

        // Register feature flags as singleton
        services.AddSingleton(flags);

        // Log enabled features
        Console.WriteLine($"[Infrastructure Engines] {flags.GetStatusSummary()}");

        // Embedding Cache - after successful embedding generation
        if (flags.EmbeddingCacheEnabled)
        {
            services.AddSingleton<IEmbeddingCache>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<EmbeddingCache>>();
                return new EmbeddingCache(dataSource, logger);
            });
            Console.WriteLine("  [+] EmbeddingCache registered");
        }
        else
        {
            services.AddSingleton<IEmbeddingCache, NoOpEmbeddingCache>();
        }

        // Inference Session Manager - during reasoning operations
        if (flags.DeterministicInferenceEnabled)
        {
            services.AddSingleton<IInferenceSessionManager>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<InferenceSessionManager>>();
                return new InferenceSessionManager(dataSource, logger);
            });
            Console.WriteLine("  [+] InferenceSessionManager registered");
        }
        else
        {
            services.AddSingleton<IInferenceSessionManager, NoOpInferenceSessionManager>();
        }

        // Memory Compiler - after memory ingest/update
        if (flags.MemoryCompilationEnabled)
        {
            services.AddSingleton<IMemoryCompiler>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var embeddingService = sp.GetRequiredService<IEmbeddingService>();
                var logger = sp.GetRequiredService<ILogger<MemoryCompiler>>();
                return new MemoryCompiler(dataSource, embeddingService, logger);
            });
            Console.WriteLine("  [+] MemoryCompiler registered");
        }
        else
        {
            services.AddSingleton<IMemoryCompiler, NoOpMemoryCompiler>();
        }

        // Context Budget Optimizer - during prompt/context assembly
        if (flags.ContextOptimizationEnabled)
        {
            services.AddSingleton<IContextBudgetOptimizer>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<ContextBudgetOptimizer>>();
                return new ContextBudgetOptimizer(dataSource, logger);
            });
            Console.WriteLine("  [+] ContextBudgetOptimizer registered");
        }
        else
        {
            services.AddSingleton<IContextBudgetOptimizer, NoOpContextBudgetOptimizer>();
        }

        // Local Encryption Service - during write/read boundaries to DB
        if (flags.LocalEncryptionEnabled)
        {
            services.AddSingleton<ILocalEncryption>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<LocalEncryptionService>>();
                return new LocalEncryptionService(dataSource, logger);
            });
            Console.WriteLine("  [+] LocalEncryptionService registered");
        }
        else
        {
            services.AddSingleton<ILocalEncryption, NoOpLocalEncryption>();
        }

        // Dual Pass Reasoning Engine - wraps existing reasoning pipeline
        if (flags.DualPassReasoningEnabled)
        {
            services.AddSingleton<IDualPassReasoning>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var embeddingService = sp.GetRequiredService<IEmbeddingService>();
                var retrievalEngine = sp.GetRequiredService<IHybridRetrievalEngine>();
                var logger = sp.GetRequiredService<ILogger<DualPassReasoningEngine>>();
                return new DualPassReasoningEngine(dataSource, embeddingService, retrievalEngine, logger);
            });
            Console.WriteLine("  [+] DualPassReasoningEngine registered");
        }
        else
        {
            services.AddSingleton<IDualPassReasoning, NoOpDualPassReasoning>();
        }

        // Graph Recrawl Service - triggered by self-healing engine
        if (flags.GraphRecrawlEnabled)
        {
            services.AddSingleton<IGraphRecrawlService>(sp =>
            {
                var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                    ?? "Host=localhost;Database=contextdb;Username=postgres;Password=postgres";
                var entityExtractor = sp.GetRequiredService<IEntityExtractionService>();
                var graphStore = sp.GetRequiredService<IKnowledgeGraphStore>();
                var logger = sp.GetRequiredService<ILogger<GraphRecrawlService>>();
                return new GraphRecrawlService(connectionString, entityExtractor, graphStore, logger);
            });
            Console.WriteLine("  [+] GraphRecrawlService registered");
        }
        else
        {
            services.AddSingleton<IGraphRecrawlService, NoOpGraphRecrawlService>();
        }

        // Time Travel Debugger - only triggered by explicit API calls
        if (flags.TimeTravelEnabled)
        {
            services.AddSingleton<ITimeTravelDebugger>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<TimeTravelDebugger>>();
                return new TimeTravelDebugger(dataSource, logger);
            });
            Console.WriteLine("  [+] TimeTravelDebugger registered");
        }
        else
        {
            services.AddSingleton<ITimeTravelDebugger, NoOpTimeTravelDebugger>();
        }

        return services;
    }
}
