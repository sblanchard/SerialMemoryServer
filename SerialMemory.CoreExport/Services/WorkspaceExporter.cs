using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SerialMemory.CoreExport.Models;

namespace SerialMemory.CoreExport.Services;

public class WorkspaceExporter(
    CoreApiClient apiClient,
    IOptions<CoreConfig> config,
    ILogger<WorkspaceExporter> logger)
{
    private readonly CoreConfig _config = config.Value;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const string StateFileName = "export-state.json";

    /// <summary>
    /// Export all memories using CORE search API
    /// Uses POST /v1/search with query="*" to get all memories
    /// </summary>
    public async Task ExportAllWorkspaces(CancellationToken ct = default)
    {
        logger.LogInformation("Starting CORE export to {Path}", _config.ExportPath);

        // Create export directory
        Directory.CreateDirectory(_config.ExportPath);

        // Load resume state
        var state = await LoadState();

        // Export all memories via search endpoint
        await ExportAllDirect(state, ct);
    }

    private async Task ExportByWorkspaces(List<Workspace> workspaces, ExportState state, CancellationToken ct)
    {
        foreach (var workspace in workspaces)
        {
            // Skip completed workspaces
            if (state.CompletedWorkspaces.Contains(workspace.Id))
            {
                logger.LogInformation("Skipping already completed workspace: {Name} ({Id})",
                    workspace.Name, workspace.Id);
                continue;
            }

            // Resume from last cursor if this is the last workspace we were working on
            string? startCursor = null;
            if (state.LastWorkspace == workspace.Id)
            {
                startCursor = state.LastCursor;
                logger.LogInformation("Resuming workspace {Name} from cursor {Cursor}",
                    workspace.Name, startCursor);
            }

            await ExportWorkspace(workspace, startCursor, state, ct);

            // Mark workspace as completed
            state.CompletedWorkspaces.Add(workspace.Id);
            state.LastWorkspace = null;
            state.LastCursor = null;
            await SaveState(state);
        }

        logger.LogInformation("Export completed successfully!");
    }

    /// <summary>
    /// Export all data directly using CORE search API
    /// POST /v1/search with empty query
    /// </summary>
    private async Task ExportAllDirect(ExportState state, CancellationToken ct)
    {
        logger.LogInformation("Exporting all data via CORE search API");

        var response = await apiClient.ExportAll(null, ct);

        if (response == null)
        {
            logger.LogWarning("Empty response from search endpoint");
            return;
        }

        var allMemories = response.Memories;
        var allEntities = response.Entities;
        var allRelations = response.Relations;

        logger.LogInformation("Fetched {Count} memories from CORE", allMemories.Count);

        // Create full export
        var workspaceDir = Path.Combine(_config.ExportPath, "full-export");
        Directory.CreateDirectory(workspaceDir);

        var export = new WorkspaceExport
        {
            WorkspaceId = "full-export",
            WorkspaceName = "Full CORE Export",
            ExportedAtUtc = DateTime.UtcNow,
            MemoryCount = allMemories.Count,
            Memories = allMemories.Select(m => new MemoryItem
            {
                Id = m.Id,
                Content = m.Content,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
                Metadata = m.Metadata,
                Embedding = m.Embedding,
                Relationships = m.Relations?.Select(r => new Relationship
                {
                    Type = r.RelationType,
                    TargetId = r.To
                }).ToList()
            }).ToList()
        };

        await WriteJsonExport(workspaceDir, export);
        await WriteCsvExport(workspaceDir, export);
        await WriteMetadata(workspaceDir, export);

        // Write entities and relations separately
        await WriteEntitiesExport(workspaceDir, allEntities);
        await WriteRelationsExport(workspaceDir, allRelations);

        state.CompletedWorkspaces.Add("full-export");
        state.LastCursor = null;
        await SaveState(state);

        logger.LogInformation("Full export completed: {Memories} memories, {Entities} entities, {Relations} relations",
            allMemories.Count, allEntities.Count, allRelations.Count);
    }

    private async Task WriteEntitiesExport(string workspaceDir, List<McpEntity> entities)
    {
        if (entities.Count == 0) return;

        var path = Path.Combine(workspaceDir, "entities.json");
        await using var fileStream = File.Create(path);
        await JsonSerializer.SerializeAsync(fileStream, entities, _jsonOptions);
        logger.LogInformation("Written entities export: {Path} ({Count} entities)", path, entities.Count);
    }

    private async Task WriteRelationsExport(string workspaceDir, List<McpRelation> relations)
    {
        if (relations.Count == 0) return;

        var path = Path.Combine(workspaceDir, "relations.json");
        await using var fileStream = File.Create(path);
        await JsonSerializer.SerializeAsync(fileStream, relations, _jsonOptions);
        logger.LogInformation("Written relations export: {Path} ({Count} relations)", path, relations.Count);
    }

    private async Task ExportWorkspace(
        Workspace workspace,
        string? startCursor,
        ExportState state,
        CancellationToken ct)
    {
        logger.LogInformation("Exporting workspace: {Name} ({Id})", workspace.Name, workspace.Id);

        // Create workspace directory
        var workspaceDir = Path.Combine(_config.ExportPath, $"workspace-{workspace.Id}");
        Directory.CreateDirectory(workspaceDir);

        var memories = new List<MemoryItem>();
        string? cursor = startCursor;
        int totalFetched = 0;

        // Paginate through all memories
        while (true)
        {
            var page = await apiClient.GetMemories(workspace.Id, cursor, ct);

            if (page.Items.Count > 0)
            {
                memories.AddRange(page.Items);
                totalFetched += page.Items.Count;

                logger.LogInformation("Fetched {Count} memories (total: {Total})",
                    page.Items.Count, totalFetched);
            }

            // Update state
            state.LastWorkspace = workspace.Id;
            state.LastCursor = page.NextCursor;
            state.LastUpdated = DateTime.UtcNow;
            await SaveState(state);

            // Check if we're done
            if (page.NextCursor == null)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        logger.LogInformation("Completed fetching {Total} memories for workspace {Name}",
            totalFetched, workspace.Name);

        // Create export object
        var export = new WorkspaceExport
        {
            WorkspaceId = workspace.Id,
            WorkspaceName = workspace.Name,
            ExportedAtUtc = DateTime.UtcNow,
            MemoryCount = memories.Count,
            Memories = memories
        };

        // Write JSON
        await WriteJsonExport(workspaceDir, export);

        // Write CSV
        await WriteCsvExport(workspaceDir, export);

        // Write metadata
        await WriteMetadata(workspaceDir, export);

        logger.LogInformation("Exported workspace {Name} to {Path}", workspace.Name, workspaceDir);
    }

    private async Task WriteJsonExport(string workspaceDir, WorkspaceExport export)
    {
        var jsonPath = Path.Combine(workspaceDir, "memories.json");

        await using var fileStream = File.Create(jsonPath);
        await JsonSerializer.SerializeAsync(fileStream, export, _jsonOptions);

        logger.LogInformation("Written JSON export: {Path}", jsonPath);
    }

    private async Task WriteCsvExport(string workspaceDir, WorkspaceExport export)
    {
        var csvPath = Path.Combine(workspaceDir, "memories.csv");

        await using var writer = new StreamWriter(csvPath);
        await using var csv = new CsvHelper.CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture);

        // Write headers
        csv.WriteField("Id");
        csv.WriteField("Content");
        csv.WriteField("CreatedAt");
        csv.WriteField("UpdatedAt");
        csv.WriteField("Metadata");
        await csv.NextRecordAsync();

        // Write records
        foreach (var memory in export.Memories)
        {
            csv.WriteField(memory.Id);
            csv.WriteField(memory.Content);
            csv.WriteField(memory.CreatedAt);
            csv.WriteField(memory.UpdatedAt);
            csv.WriteField(memory.Metadata != null
                ? JsonSerializer.Serialize(memory.Metadata)
                : string.Empty);
            await csv.NextRecordAsync();
        }

        logger.LogInformation("Written CSV export: {Path}", csvPath);
    }

    private async Task WriteMetadata(string workspaceDir, WorkspaceExport export)
    {
        var metadataPath = Path.Combine(workspaceDir, "metadata.json");

        var metadata = new
        {
            export.WorkspaceId,
            export.WorkspaceName,
            export.ExportedAtUtc,
            export.MemoryCount,
            HasEmbeddings = export.Memories.Any(m => m.Embedding != null),
            HasRelationships = export.Memories.Any(m => m.Relationships?.Count > 0)
        };

        await using var fileStream = File.Create(metadataPath);
        await JsonSerializer.SerializeAsync(fileStream, metadata, _jsonOptions);

        logger.LogInformation("Written metadata: {Path}", metadataPath);
    }

    private async Task<ExportState> LoadState()
    {
        var statePath = Path.Combine(_config.ExportPath, StateFileName);

        if (!File.Exists(statePath))
        {
            logger.LogInformation("No existing state found, starting fresh export");
            return new ExportState();
        }

        try
        {
            await using var fileStream = File.OpenRead(statePath);
            var state = await JsonSerializer.DeserializeAsync<ExportState>(fileStream);

            logger.LogInformation("Loaded existing state: workspace={LastWorkspace}, cursor={LastCursor}",
                state?.LastWorkspace ?? "null",
                state?.LastCursor ?? "null");

            return state ?? new ExportState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load state, starting fresh");
            return new ExportState();
        }
    }

    private async Task SaveState(ExportState state)
    {
        var statePath = Path.Combine(_config.ExportPath, StateFileName);
        state.LastUpdated = DateTime.UtcNow;

        await using var fileStream = File.Create(statePath);
        await JsonSerializer.SerializeAsync(fileStream, state, _jsonOptions);
    }
}
