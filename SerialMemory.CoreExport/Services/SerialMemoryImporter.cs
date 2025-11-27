using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.CoreExport.Models;

namespace SerialMemory.CoreExport.Services;

public class SerialMemoryImporter
{
    private readonly IKnowledgeGraphStore _store;
    private readonly IEmbeddingService _embeddingService;
    private readonly IEntityExtractionService _entityExtraction;
    private readonly CoreConfig _config;
    private readonly ILogger<SerialMemoryImporter> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public SerialMemoryImporter(
        IKnowledgeGraphStore store,
        IEmbeddingService embeddingService,
        IEntityExtractionService entityExtraction,
        IOptions<CoreConfig> config,
        ILogger<SerialMemoryImporter> logger)
    {
        _store = store;
        _embeddingService = embeddingService;
        _entityExtraction = entityExtraction;
        _config = config.Value;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task ImportFromExportDirectory(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting import from {Path} to SerialMemory contextdb", _config.ExportPath);

        if (!Directory.Exists(_config.ExportPath))
        {
            _logger.LogError("Export directory not found: {Path}", _config.ExportPath);
            throw new DirectoryNotFoundException($"Export directory not found: {_config.ExportPath}");
        }

        // Find all workspace directories (workspace-* or full-export)
        var workspaceDirs = Directory.GetDirectories(_config.ExportPath, "workspace-*").ToList();

        // Also check for full-export directory
        var fullExportDir = Path.Combine(_config.ExportPath, "full-export");
        if (Directory.Exists(fullExportDir))
        {
            workspaceDirs.Add(fullExportDir);
        }

        _logger.LogInformation("Found {Count} directories to import", workspaceDirs.Count);

        int totalMemories = 0;
        int totalEntities = 0;
        int totalRelationships = 0;

        foreach (var workspaceDir in workspaceDirs)
        {
            var stats = await ImportWorkspace(workspaceDir, ct);
            totalMemories += stats.Memories;
            totalEntities += stats.Entities;
            totalRelationships += stats.Relationships;
        }

        _logger.LogInformation(
            "Import completed! Imported {Memories} memories, {Entities} entities, {Relationships} relationships",
            totalMemories, totalEntities, totalRelationships);
    }

    private async Task<ImportStats> ImportWorkspace(string workspaceDir, CancellationToken ct)
    {
        var workspaceName = Path.GetFileName(workspaceDir);
        _logger.LogInformation("Importing workspace: {Name}", workspaceName);

        var stats = new ImportStats();

        // First, import entities from entities.json if it exists
        var entitiesPath = Path.Combine(workspaceDir, "entities.json");
        var entityNameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(entitiesPath))
        {
            _logger.LogInformation("Found entities.json, importing entities first...");
            await using var entitiesStream = File.OpenRead(entitiesPath);
            var entities = await JsonSerializer.DeserializeAsync<List<McpEntity>>(entitiesStream, _jsonOptions, ct);

            if (entities != null)
            {
                _logger.LogInformation("Importing {Count} entities", entities.Count);
                foreach (var mcpEntity in entities)
                {
                    try
                    {
                        var entity = new Entity
                        {
                            Name = mcpEntity.Name,
                            EntityType = mcpEntity.EntityType,
                            CreatedAt = DateTime.UtcNow
                        };
                        var entityId = await _store.CreateEntityAsync(entity, ct);
                        entityNameToId[mcpEntity.Name] = entityId;
                        stats.Entities++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import entity: {Name}", mcpEntity.Name);
                    }
                }
                _logger.LogInformation("Imported {Count} entities", stats.Entities);
            }
        }

        // Import relations from relations.json if it exists
        var relationsPath = Path.Combine(workspaceDir, "relations.json");
        var episodeIdToMemoryId = new Dictionary<string, Guid>();

        if (File.Exists(relationsPath))
        {
            _logger.LogInformation("Found relations.json, will import after memories...");
        }

        // Read the memories.json file
        var memoriesPath = Path.Combine(workspaceDir, "memories.json");

        if (!File.Exists(memoriesPath))
        {
            _logger.LogWarning("No memories.json found in {Path}, skipping", workspaceDir);
            return stats;
        }

        await using var fileStream = File.OpenRead(memoriesPath);
        var export = await JsonSerializer.DeserializeAsync<WorkspaceExport>(fileStream, _jsonOptions, ct);

        if (export == null || export.Memories.Count == 0)
        {
            _logger.LogWarning("No memories found in {Path}, skipping", memoriesPath);
            return stats;
        }

        _logger.LogInformation("Processing {Count} memories from workspace {Name}",
            export.Memories.Count, export.WorkspaceName);

        // Create a session for this import
        var session = new ConversationSession
        {
            Id = Guid.NewGuid(),
            SessionName = $"CORE Import - {export.WorkspaceName}",
            ClientType = "CoreImporter",
            StartedAt = DateTime.UtcNow
        };
        var sessionId = await _store.CreateConversationSessionAsync(session, ct);

        // Import each memory, tracking episode UUID to memory ID mapping
        foreach (var coreMemory in export.Memories)
        {
            try
            {
                var memoryId = await ImportMemory(coreMemory, sessionId, entityNameToId, stats, ct);
                if (!string.IsNullOrEmpty(coreMemory.Id))
                {
                    episodeIdToMemoryId[coreMemory.Id] = memoryId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import memory {Id}: {Content}",
                    coreMemory.Id, coreMemory.Content.Substring(0, Math.Min(50, coreMemory.Content.Length)));
            }
        }

        // Now import relations from relations.json
        if (File.Exists(relationsPath))
        {
            _logger.LogInformation("Importing relations from relations.json...");
            await using var relationsStream = File.OpenRead(relationsPath);
            var relations = await JsonSerializer.DeserializeAsync<List<McpRelation>>(relationsStream, _jsonOptions, ct);

            if (relations != null)
            {
                _logger.LogInformation("Processing {Count} relations", relations.Count);
                foreach (var mcpRelation in relations)
                {
                    try
                    {
                        // Relation From is episodeUUID, To is entity name
                        if (episodeIdToMemoryId.TryGetValue(mcpRelation.From, out var memoryId) &&
                            entityNameToId.TryGetValue(mcpRelation.To, out var entityId))
                        {
                            // Link memory to entity
                            await _store.LinkMemoryToEntityAsync(memoryId, entityId, 1.0f, ct);
                            stats.Relationships++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to import relation: {From} -> {To}", mcpRelation.From, mcpRelation.To);
                    }
                }
                _logger.LogInformation("Imported {Count} relations", stats.Relationships);
            }
        }

        // End session
        await _store.EndConversationSessionAsync(sessionId, ct);

        _logger.LogInformation("Completed importing workspace {Name}: {Stats}",
            export.WorkspaceName, stats);

        return stats;
    }

    private async Task<Guid> ImportMemory(
        MemoryItem coreMemory,
        Guid sessionId,
        Dictionary<string, Guid> entityNameToId,
        ImportStats stats,
        CancellationToken ct)
    {
        // Generate embedding for the memory content
        var embedding = await _embeddingService.EmbedTextAsync(coreMemory.Content, ct);

        // Create the memory record
        var memory = new Memory
        {
            Content = coreMemory.Content,
            Embedding = embedding,
            Source = "CORE",
            CreatedAt = coreMemory.CreatedAt,
            UpdatedAt = coreMemory.UpdatedAt ?? coreMemory.CreatedAt,
            ConversationSessionId = sessionId
        };

        // Add metadata
        if (coreMemory.Metadata != null)
        {
            memory.Metadata = coreMemory.Metadata;
        }

        // Store the memory
        memory.Id = await _store.CreateMemoryAsync(memory, ct);
        stats.Memories++;

        if (stats.Memories % 50 == 0)
        {
            _logger.LogInformation("Imported {Count} memories...", stats.Memories);
        }

        return memory.Id;
    }
    private class ImportStats
    {
        public int Memories { get; set; }
        public int Entities { get; set; }
        public int Relationships { get; set; }

        public override string ToString() =>
            $"{Memories} memories, {Entities} entities, {Relationships} relationships";
    }
}
