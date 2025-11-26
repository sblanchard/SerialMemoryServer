using FluentAssertions;
using Moq;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using Xunit;

namespace SerialMemory.Tests.Services;

public class KnowledgeGraphServiceTests
{
    private readonly Mock<IKnowledgeGraphStore> _storeMock;
    private readonly Mock<IEmbeddingService> _embeddingServiceMock;
    private readonly Mock<IEntityExtractionService> _entityExtractionServiceMock;
    private readonly KnowledgeGraphService _service;

    public KnowledgeGraphServiceTests()
    {
        _storeMock = new Mock<IKnowledgeGraphStore>();
        _embeddingServiceMock = new Mock<IEmbeddingService>();
        _entityExtractionServiceMock = new Mock<IEntityExtractionService>();

        _service = new KnowledgeGraphService(
            _storeMock.Object,
            _embeddingServiceMock.Object,
            _entityExtractionServiceMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_NullStore_ThrowsArgumentNullException()
    {
        var act = () => new KnowledgeGraphService(
            null!,
            _embeddingServiceMock.Object,
            _entityExtractionServiceMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("store");
    }

    [Fact]
    public void Constructor_NullEmbeddingService_ThrowsArgumentNullException()
    {
        var act = () => new KnowledgeGraphService(
            _storeMock.Object,
            null!,
            _entityExtractionServiceMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("embeddingService");
    }

    [Fact]
    public void Constructor_NullEntityExtractionService_ThrowsArgumentNullException()
    {
        var act = () => new KnowledgeGraphService(
            _storeMock.Object,
            _embeddingServiceMock.Object,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("entityExtractionService");
    }

    #endregion

    #region IngestMemoryAsync Tests

    [Fact]
    public async Task IngestMemoryAsync_BasicMemory_CreatesMemoryWithEmbedding()
    {
        // Arrange
        var content = "John works at Microsoft";
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memoryId = Guid.CreateVersion7();

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        _entityExtractionServiceMock
            .Setup(x => x.ExtractAllAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ExtractedEntity>(), new List<ExtractedRelationship>()));

        // Act
        var result = await _service.IngestMemoryAsync(content);

        // Assert
        result.MemoryId.Should().Be(memoryId);
        _embeddingServiceMock.Verify(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(x => x.CreateMemoryAsync(
            It.Is<Memory>(m => m.Content == content && m.Embedding == embedding),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestMemoryAsync_WithEntities_ExtractsAndStoresEntities()
    {
        // Arrange
        var content = "John works at Microsoft";
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memoryId = Guid.CreateVersion7();
        var entityId = Guid.CreateVersion7();

        var extractedEntities = new List<ExtractedEntity>
        {
            new("John", "PERSON", 0, 4, 0.9f),
            new("Microsoft", "ORG", 15, 24, 0.95f)
        };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        _storeMock
            .Setup(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityId);

        _entityExtractionServiceMock
            .Setup(x => x.ExtractAllAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync((extractedEntities, new List<ExtractedRelationship>()));

        // Act
        var result = await _service.IngestMemoryAsync(content);

        // Assert
        result.EntitiesCreated.Should().Be(2);
        result.Entities.Should().HaveCount(2);
        _storeMock.Verify(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _storeMock.Verify(x => x.LinkMemoryToEntityAsync(memoryId, entityId, It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IngestMemoryAsync_WithRelationships_CreatesRelationships()
    {
        // Arrange
        var content = "John works at Microsoft";
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memoryId = Guid.CreateVersion7();
        var johnEntityId = Guid.CreateVersion7();
        var msEntityId = Guid.CreateVersion7();

        var extractedEntities = new List<ExtractedEntity>
        {
            new("John", "PERSON", 0, 4, 0.9f),
            new("Microsoft", "ORG", 15, 24, 0.95f)
        };

        var extractedRelationships = new List<ExtractedRelationship>
        {
            new("John", "Microsoft", "WORKS_AT", 0.85f)
        };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        _storeMock
            .SetupSequence(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(johnEntityId)
            .ReturnsAsync(msEntityId);

        _entityExtractionServiceMock
            .Setup(x => x.ExtractAllAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync((extractedEntities, extractedRelationships));

        // Act
        var result = await _service.IngestMemoryAsync(content);

        // Assert
        result.RelationshipsCreated.Should().Be(1);
        result.Relationships.Should().HaveCount(1);
        result.Relationships[0].Source.Should().Be("John");
        result.Relationships[0].Target.Should().Be("Microsoft");
        result.Relationships[0].Type.Should().Be("WORKS_AT");
    }

    [Fact]
    public async Task IngestMemoryAsync_ExtractEntitiesFalse_SkipsExtraction()
    {
        // Arrange
        var content = "John works at Microsoft";
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memoryId = Guid.CreateVersion7();

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        // Act
        var result = await _service.IngestMemoryAsync(content, extractEntities: false);

        // Assert
        result.EntitiesCreated.Should().Be(0);
        _entityExtractionServiceMock.Verify(x => x.ExtractAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IngestMemoryAsync_WithMetadata_PassesMetadataToMemory()
    {
        // Arrange
        var content = "Test content";
        var embedding = new float[] { 0.1f };
        var memoryId = Guid.CreateVersion7();
        var metadata = new Dictionary<string, object> { ["key"] = "value" };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        _entityExtractionServiceMock
            .Setup(x => x.ExtractAllAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ExtractedEntity>(), new List<ExtractedRelationship>()));

        // Act
        await _service.IngestMemoryAsync(content, metadata: metadata);

        // Assert
        _storeMock.Verify(x => x.CreateMemoryAsync(
            It.Is<Memory>(m => m.Metadata == metadata),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region SearchMemoriesAsync Tests

    [Fact]
    public async Task SearchMemoriesAsync_SemanticMode_UsesEmbeddingSearch()
    {
        // Arrange
        var query = "test query";
        var queryEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memories = new List<Memory>
        {
            new() { Id = Guid.CreateVersion7(), Content = "Result 1" },
            new() { Id = Guid.CreateVersion7(), Content = "Result 2" }
        };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Semantic);

        // Assert
        results.Should().HaveCount(2);
        _embeddingServiceMock.Verify(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchMemoriesAsync_TextMode_UsesTextSearch()
    {
        // Arrange
        var query = "test query";
        var memories = new List<Memory>
        {
            new() { Id = Guid.CreateVersion7(), Content = "Result 1" }
        };

        _storeMock
            .Setup(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Text);

        // Assert
        results.Should().HaveCount(1);
        _storeMock.Verify(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()), Times.Once);
        _embeddingServiceMock.Verify(x => x.EmbedTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchMemoriesAsync_HybridMode_UsesBothSearchMethods()
    {
        // Arrange
        var query = "test query";
        var queryEmbedding = new float[] { 0.1f };
        var semanticMemory = new Memory { Id = Guid.CreateVersion7(), Content = "Semantic Result" };
        var textMemory = new Memory { Id = Guid.CreateVersion7(), Content = "Text Result" };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { semanticMemory });

        _storeMock
            .Setup(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { textMemory });

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Hybrid);

        // Assert
        results.Should().HaveCount(2);
        _storeMock.Verify(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchMemoriesAsync_HybridMode_DeduplicatesResults()
    {
        // Arrange
        var query = "test query";
        var queryEmbedding = new float[] { 0.1f };
        var sharedMemory = new Memory { Id = Guid.CreateVersion7(), Content = "Shared Result" };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { sharedMemory });

        _storeMock
            .Setup(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { sharedMemory }); // Same memory

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Hybrid);

        // Assert
        results.Should().HaveCount(1); // Deduplicated
    }

    [Fact]
    public async Task SearchMemoriesAsync_WithEntities_IncludesEntityInfo()
    {
        // Arrange
        var query = "test query";
        var queryEmbedding = new float[] { 0.1f };
        var memoryId = Guid.CreateVersion7();
        var entityId = Guid.CreateVersion7();
        var memory = new Memory { Id = memoryId, Content = "Result" };
        var entity = new Entity { Id = entityId, Name = "John", EntityType = "PERSON" };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { memory });

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity> { entity });

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Semantic, includeEntities: true);

        // Assert
        results[0].Entities.Should().HaveCount(1);
        results[0].Entities[0].Name.Should().Be("John");
        results[0].Entities[0].Type.Should().Be("PERSON");
    }

    [Fact]
    public async Task SearchMemoriesAsync_SemanticMode_PreservesSimilarityScore()
    {
        // Arrange
        var query = "test query";
        var queryEmbedding = new float[] { 0.1f };
        var memory = new Memory
        {
            Id = Guid.CreateVersion7(),
            Content = "Result",
            Similarity = 0.92f // Similarity score from store
        };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(queryEmbedding, 10, 0.7f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { memory });

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Semantic);

        // Assert
        results.Should().HaveCount(1);
        results[0].Similarity.Should().Be(0.92f);
    }

    [Fact]
    public async Task SearchMemoriesAsync_TextMode_PreservesRankScore()
    {
        // Arrange
        var query = "test query";
        var memory = new Memory
        {
            Id = Guid.CreateVersion7(),
            Content = "Result",
            Rank = 1.5f // Rank score from text search
        };

        _storeMock
            .Setup(x => x.SearchMemoriesByTextAsync(query, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { memory });

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var results = await _service.SearchMemoriesAsync(query, SearchMode.Text);

        // Assert
        results.Should().HaveCount(1);
        results[0].Rank.Should().Be(1.5f);
    }

    #endregion

    #region Session Operations Tests

    [Fact]
    public async Task CreateConversationSessionAsync_CreatesSession()
    {
        // Arrange
        var sessionId = Guid.CreateVersion7();
        var sessionName = "Test Session";
        var clientType = "test-client";

        _storeMock
            .Setup(x => x.CreateConversationSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessionId);

        // Act
        var result = await _service.CreateConversationSessionAsync(sessionName, clientType);

        // Assert
        result.Should().Be(sessionId);
        _storeMock.Verify(x => x.CreateConversationSessionAsync(
            It.Is<ConversationSession>(s => s.SessionName == sessionName && s.ClientType == clientType),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndConversationSessionAsync_EndsSession()
    {
        // Arrange
        var sessionId = Guid.CreateVersion7();

        // Act
        await _service.EndConversationSessionAsync(sessionId);

        // Assert
        _storeMock.Verify(x => x.EndConversationSessionAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetRecentSessionsAsync_ReturnsRecentSessions()
    {
        // Arrange
        var sessions = new List<ConversationSession>
        {
            new() { Id = Guid.CreateVersion7(), SessionName = "Session 1" },
            new() { Id = Guid.CreateVersion7(), SessionName = "Session 2" }
        };

        _storeMock
            .Setup(x => x.GetRecentSessionsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);

        // Act
        var result = await _service.GetRecentSessionsAsync();

        // Assert
        result.Should().HaveCount(2);
        result[0].SessionName.Should().Be("Session 1");
    }

    #endregion

    #region User Persona Tests

    [Fact]
    public async Task GetUserPersonaAsync_ReturnsPersona()
    {
        // Arrange
        var userId = "test-user";
        var persona = new Dictionary<string, Dictionary<string, object>>
        {
            ["preferences"] = new Dictionary<string, object>
            {
                ["theme"] = new Dictionary<string, object>
                {
                    ["value"] = "dark",
                    ["confidence"] = 0.9f
                }
            }
        };

        _storeMock
            .Setup(x => x.GetUserPersonaAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(persona);

        // Act
        var result = await _service.GetUserPersonaAsync(userId);

        // Assert
        result.Should().ContainKey("preferences");
        result["preferences"].Should().ContainKey("theme");
    }

    [Fact]
    public async Task SetUserPersonaAttributeAsync_SetsAttribute()
    {
        // Arrange
        var userId = "test-user";
        var attributeType = "preferences";
        var attributeKey = "theme";
        var attributeValue = "dark";

        // Act
        await _service.SetUserPersonaAttributeAsync(attributeType, attributeKey, attributeValue, userId: userId);

        // Assert
        _storeMock.Verify(x => x.SetUserPersonaAttributeAsync(
            It.Is<UserPersona>(p =>
                p.UserId == userId &&
                p.AttributeType == attributeType &&
                p.AttributeKey == attributeKey &&
                p.AttributeValue == attributeValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ImportFromCoreAsync Tests

    [Fact]
    public async Task ImportFromCoreAsync_EmptyData_ReturnsEmptyResult()
    {
        // Arrange
        var coreData = new CoreExportData();

        // Act
        var result = await _service.ImportFromCoreAsync(coreData);

        // Assert
        result.EntitiesImported.Should().Be(0);
        result.RelationsImported.Should().Be(0);
        result.ObservationsImported.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportFromCoreAsync_WithEntities_ImportsEntities()
    {
        // Arrange
        var entityId = Guid.CreateVersion7();
        var coreData = new CoreExportData
        {
            Entities = new List<CoreEntity>
            {
                new() { Name = "John", EntityType = "PERSON" },
                new() { Name = "Microsoft", EntityType = "ORG" }
            }
        };

        _storeMock
            .Setup(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityId);

        // Act
        var result = await _service.ImportFromCoreAsync(coreData);

        // Assert
        result.EntitiesImported.Should().Be(2);
        _storeMock.Verify(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ImportFromCoreAsync_WithObservations_ImportsAsMemories()
    {
        // Arrange
        var entityId = Guid.CreateVersion7();
        var memoryId = Guid.CreateVersion7();
        var embedding = new float[] { 0.1f };

        var coreData = new CoreExportData
        {
            Entities = new List<CoreEntity>
            {
                new()
                {
                    Name = "John",
                    EntityType = "PERSON",
                    Observations = new List<string> { "Works at Microsoft", "Lives in Seattle" }
                }
            }
        };

        _storeMock
            .Setup(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityId);

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryId);

        // Act
        var result = await _service.ImportFromCoreAsync(coreData);

        // Assert
        result.EntitiesImported.Should().Be(1);
        result.ObservationsImported.Should().Be(2);
        _storeMock.Verify(x => x.CreateMemoryAsync(It.IsAny<Memory>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _storeMock.Verify(x => x.LinkMemoryToEntityAsync(memoryId, entityId, 1.0f, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ImportFromCoreAsync_WithRelations_ImportsRelationships()
    {
        // Arrange
        var johnId = Guid.CreateVersion7();
        var microsoftId = Guid.CreateVersion7();

        var coreData = new CoreExportData
        {
            Entities = new List<CoreEntity>
            {
                new() { Name = "John", EntityType = "PERSON" },
                new() { Name = "Microsoft", EntityType = "ORG" }
            },
            Relations = new List<CoreRelation>
            {
                new() { From = "John", To = "Microsoft", RelationType = "works at" }
            }
        };

        _storeMock
            .SetupSequence(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(johnId)
            .ReturnsAsync(microsoftId);

        // Act
        var result = await _service.ImportFromCoreAsync(coreData);

        // Assert
        result.RelationsImported.Should().Be(1);
        _storeMock.Verify(x => x.CreateRelationshipAsync(
            It.Is<EntityRelationship>(r =>
                r.SourceEntityId == johnId &&
                r.TargetEntityId == microsoftId &&
                r.RelationshipType == "WORKS_AT"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportFromCoreAsync_MissingEntityForRelation_AddsError()
    {
        // Arrange
        var johnId = Guid.CreateVersion7();

        var coreData = new CoreExportData
        {
            Entities = new List<CoreEntity>
            {
                new() { Name = "John", EntityType = "PERSON" }
            },
            Relations = new List<CoreRelation>
            {
                new() { From = "John", To = "NonExistent", RelationType = "knows" }
            }
        };

        _storeMock
            .Setup(x => x.CreateEntityAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(johnId);

        // Act
        var result = await _service.ImportFromCoreAsync(coreData);

        // Assert
        result.RelationsImported.Should().Be(0);
        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Contain("NonExistent");
    }

    #endregion

    #region MultiHopSearchAsync Tests

    [Fact]
    public async Task MultiHopSearchAsync_ReturnsInitialResults()
    {
        // Arrange
        var query = "John";
        var memoryId = Guid.CreateVersion7();
        var entityId = Guid.CreateVersion7();
        var embedding = new float[] { 0.1f };

        _embeddingServiceMock
            .Setup(x => x.EmbedTextAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _storeMock
            .Setup(x => x.SearchMemoriesByEmbeddingAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory> { new() { Id = memoryId, Content = "John works at Microsoft" } });

        _storeMock
            .Setup(x => x.SearchMemoriesByTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Memory>());

        _storeMock
            .Setup(x => x.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity> { new() { Id = entityId, Name = "John", EntityType = "PERSON" } });

        _storeMock
            .Setup(x => x.GetRelationshipsForEntityAsync(entityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntityRelationship>());

        // Act
        var result = await _service.MultiHopSearchAsync(query, hops: 1);

        // Assert
        result.Memories.Should().HaveCount(1);
        result.Entities.Should().HaveCount(1);
        result.Hops.Should().Be(1);
    }

    #endregion
}
