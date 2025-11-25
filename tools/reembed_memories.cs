#!/usr/bin/env dotnet-script
// Re-embedding tool for SerialMemoryServer
// Regenerates embeddings for all memories when switching to a new model
//
// Usage:
//   dotnet script reembed_memories.cs -- --model-path /path/to/model.onnx --vocab-path /path/to/vocab.txt
//   dotnet script reembed_memories.cs -- --http-service http://localhost:8765
//   dotnet script reembed_memories.cs -- --help

#r "nuget: Npgsql, 8.0.0"
#r "nuget: Dapper, 2.1.24"
#r "nuget: Microsoft.ML.OnnxRuntime, 1.16.3"
#r "nuget: Pgvector, 0.2.0"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Pgvector;

class ReembedTool
{
    private readonly string _connectionString;
    private readonly Func<string, Task<float[]>> _embedFunc;
    private readonly int _embeddingDimension;
    private int _processedCount = 0;
    private int _errorCount = 0;

    public ReembedTool(string connectionString, Func<string, Task<float[]>> embedFunc, int dimension)
    {
        _connectionString = connectionString;
        _embedFunc = embedFunc;
        _embeddingDimension = dimension;
    }

    public async Task RunAsync(int batchSize = 100, bool forceAll = false)
    {
        Console.WriteLine($"Re-embedding memories with {_embeddingDimension}-dimension model...");
        Console.WriteLine($"Batch size: {batchSize}, Force all: {forceAll}");
        Console.WriteLine();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Get total count
        var whereClause = forceAll ? "" : "WHERE embedding IS NULL";
        var totalCount = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM memories {whereClause}");

        Console.WriteLine($"Total memories to process: {totalCount}");
        if (totalCount == 0)
        {
            Console.WriteLine("No memories need re-embedding.");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var processed = 0L;

        while (processed < totalCount)
        {
            // Fetch batch
            var memories = await connection.QueryAsync<MemoryRow>(
                $@"SELECT id, content FROM memories {whereClause}
                   ORDER BY created_at DESC
                   LIMIT @BatchSize OFFSET @Offset",
                new { BatchSize = batchSize, Offset = processed });

            var memoryList = memories.ToList();
            if (memoryList.Count == 0) break;

            // Process batch
            foreach (var memory in memoryList)
            {
                try
                {
                    var embedding = await _embedFunc(memory.content);

                    await connection.ExecuteAsync(
                        "UPDATE memories SET embedding = @Embedding WHERE id = @Id",
                        new { Id = memory.id, Embedding = new Pgvector.Vector(embedding) });

                    _processedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing memory {memory.id}: {ex.Message}");
                    _errorCount++;
                }

                // Progress indicator
                if ((_processedCount + _errorCount) % 10 == 0)
                {
                    var progress = (double)(_processedCount + _errorCount) / totalCount * 100;
                    var elapsed = stopwatch.Elapsed;
                    var rate = _processedCount / elapsed.TotalSeconds;
                    var remaining = TimeSpan.FromSeconds((totalCount - _processedCount - _errorCount) / rate);

                    Console.Write($"\rProgress: {progress:F1}% ({_processedCount}/{totalCount}) " +
                                  $"| Rate: {rate:F1}/s | ETA: {remaining:hh\\:mm\\:ss}    ");
                }
            }

            processed += memoryList.Count;
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"Completed in {stopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Processed: {_processedCount}, Errors: {_errorCount}");
    }

    private record MemoryRow(Guid id, string content);
}

// Parse arguments
var args = Args.ToList();
string? modelPath = null;
string? vocabPath = null;
string? httpService = null;
int batchSize = 100;
bool forceAll = false;
bool showHelp = false;

for (int i = 0; i < args.Count; i++)
{
    switch (args[i])
    {
        case "--model-path":
            modelPath = args[++i];
            break;
        case "--vocab-path":
            vocabPath = args[++i];
            break;
        case "--http-service":
            httpService = args[++i];
            break;
        case "--batch-size":
            batchSize = int.Parse(args[++i]);
            break;
        case "--force-all":
            forceAll = true;
            break;
        case "--help":
        case "-h":
            showHelp = true;
            break;
    }
}

if (showHelp)
{
    Console.WriteLine(@"
Re-embedding Tool for SerialMemoryServer
=========================================

Regenerates embeddings for all memories when switching to a new ONNX model.

Usage:
  dotnet script reembed_memories.cs -- [options]

Options:
  --model-path <path>     Path to ONNX model file
  --vocab-path <path>     Path to vocab.txt file
  --http-service <url>    Use HTTP embedding service instead of ONNX
  --batch-size <n>        Number of memories per batch (default: 100)
  --force-all             Re-embed all memories, even those with embeddings
  --help, -h              Show this help message

Examples:
  # Using ONNX model
  dotnet script reembed_memories.cs -- \
    --model-path ./models/all-mpnet-base-v2/model.onnx \
    --vocab-path ./models/all-mpnet-base-v2/vocab.txt

  # Using HTTP service
  dotnet script reembed_memories.cs -- --http-service http://localhost:8765

  # Force re-embed all with larger batches
  dotnet script reembed_memories.cs -- --http-service http://localhost:8765 --force-all --batch-size 200

Environment Variables:
  POSTGRES_HOST       Database host (default: localhost)
  POSTGRES_PORT       Database port (default: 5432)
  POSTGRES_USER       Database user (default: postgres)
  POSTGRES_PASSWORD   Database password (default: postgres)
  POSTGRES_DB         Database name (default: contextdb)
");
    return;
}

// Build connection string
var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
var connectionString = $"Host={host};Port={port};Username={user};Password={password};Database={database}";

// Create embedding function based on mode
Func<string, Task<float[]>> embedFunc;
int embeddingDimension;

if (!string.IsNullOrEmpty(httpService))
{
    Console.WriteLine($"Using HTTP embedding service: {httpService}");
    var client = new HttpClient { BaseAddress = new Uri(httpService) };

    // Detect dimension
    var testResponse = await client.PostAsync("/embed",
        new StringContent(JsonSerializer.Serialize(new { text = "test" }),
        Encoding.UTF8, "application/json"));
    var testResult = await JsonSerializer.DeserializeAsync<EmbedResponse>(
        await testResponse.Content.ReadAsStreamAsync());
    embeddingDimension = testResult?.embedding?.Length ?? 384;

    Console.WriteLine($"Detected embedding dimension: {embeddingDimension}");

    embedFunc = async (text) =>
    {
        var response = await client.PostAsync("/embed",
            new StringContent(JsonSerializer.Serialize(new { text }),
            Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var result = await JsonSerializer.DeserializeAsync<EmbedResponse>(
            await response.Content.ReadAsStreamAsync());
        return result?.embedding ?? throw new Exception("Empty embedding response");
    };
}
else if (!string.IsNullOrEmpty(modelPath) && !string.IsNullOrEmpty(vocabPath))
{
    Console.WriteLine($"Using ONNX model: {modelPath}");
    Console.WriteLine("Note: For large-scale re-embedding, consider using the HTTP service for better performance.");

    // We'd need to load the actual ONNX service here
    // For simplicity, this example uses HTTP
    throw new Exception("ONNX mode requires referencing SerialMemory.ML project. Use --http-service instead.");
}
else
{
    Console.WriteLine("Error: Must specify either --model-path/--vocab-path or --http-service");
    Console.WriteLine("Run with --help for usage information.");
    return;
}

var tool = new ReembedTool(connectionString, embedFunc, embeddingDimension);
await tool.RunAsync(batchSize, forceAll);

record EmbedResponse(float[] embedding);
