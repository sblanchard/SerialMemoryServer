using FluentAssertions;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using Xunit;

namespace SerialMemory.Tests.Models;

public class ModelTests
{
    #region Memory Tests

    [Fact]
    public void Memory_DefaultValues_AreCorrect()
    {
        // Act
        var memory = new Memory
        {
            Content = null
        };

        // Assert
        memory.Id.Should().BeEmpty();
        memory.Content.Should().BeNull();
        memory.Embedding.Should().BeNull();
        memory.Source.Should().BeNull();
        memory.ConversationSessionId.Should().BeNull();
        memory.Metadata.Should().BeNull();
        memory.Similarity.Should().Be(0f);
        memory.Rank.Should().Be(0f);
    }

    [Fact]
    public void Memory_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var content = "Test content";
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var source = "test-source";
        var sessionId = Guid.CreateVersion7();
        var metadata = new Dictionary<string, object> { ["key"] = "value" };

        // Act
        var memory = new Memory
        {
            Id = id,
            Content = content,
            Embedding = embedding,
            Source = source,
            ConversationSessionId = sessionId,
            Metadata = metadata,
            Similarity = 0.95f,
            Rank = 1.5f
        };

        // Assert
        memory.Id.Should().Be(id);
        memory.Content.Should().Be(content);
        memory.Embedding.Should().BeEquivalentTo(embedding);
        memory.Source.Should().Be(source);
        memory.ConversationSessionId.Should().Be(sessionId);
        memory.Metadata.Should().BeEquivalentTo(metadata);
        memory.Similarity.Should().Be(0.95f);
        memory.Rank.Should().Be(1.5f);
    }

    #endregion

    #region Entity Tests

    [Fact]
    public void Entity_DefaultValues_AreCorrect()
    {
        // Act
        var entity = new Entity
        {
            Name = null,
            EntityType = null
        };

        // Assert
        entity.Id.Should().BeEmpty();
        entity.Name.Should().BeNull();
        entity.EntityType.Should().BeNull();
        entity.CanonicalName.Should().BeNull();
    }

    [Fact]
    public void Entity_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var memoryId = Guid.CreateVersion7();

        // Act
        var entity = new Entity
        {
            Id = id,
            Name = "John Doe",
            EntityType = "PERSON",
            CanonicalName = "john doe",
            FirstSeenMemoryId = memoryId,
            Metadata = new Dictionary<string, object> { ["role"] = "developer" }
        };

        // Assert
        entity.Id.Should().Be(id);
        entity.Name.Should().Be("John Doe");
        entity.EntityType.Should().Be("PERSON");
        entity.CanonicalName.Should().Be("john doe");
        entity.FirstSeenMemoryId.Should().Be(memoryId);
        entity.Metadata!["role"].Should().Be("developer");
    }

    #endregion

    #region EntityRelationship Tests

    [Fact]
    public void EntityRelationship_DefaultValues_AreCorrect()
    {
        // Act
        var relationship = new EntityRelationship
        {
            RelationshipType = null
        };

        // Assert
        relationship.Id.Should().BeEmpty();
        relationship.SourceEntityId.Should().BeEmpty();
        relationship.TargetEntityId.Should().BeEmpty();
        relationship.RelationshipType.Should().BeNull();
        relationship.Confidence.Should().Be(0);
    }

    [Fact]
    public void EntityRelationship_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var sourceId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var memoryId = Guid.CreateVersion7();

        // Act
        var relationship = new EntityRelationship
        {
            Id = id,
            SourceEntityId = sourceId,
            TargetEntityId = targetId,
            RelationshipType = "WORKS_AT",
            Confidence = 0.95f,
            FirstSeenMemoryId = memoryId
        };

        // Assert
        relationship.Id.Should().Be(id);
        relationship.SourceEntityId.Should().Be(sourceId);
        relationship.TargetEntityId.Should().Be(targetId);
        relationship.RelationshipType.Should().Be("WORKS_AT");
        relationship.Confidence.Should().Be(0.95f);
        relationship.FirstSeenMemoryId.Should().Be(memoryId);
    }

    #endregion

    #region ConversationSession Tests

    [Fact]
    public void ConversationSession_DefaultValues_AreCorrect()
    {
        // Act
        var session = new ConversationSession();

        // Assert
        session.Id.Should().BeEmpty();
        session.SessionName.Should().BeNull();
        session.ClientType.Should().BeNull();
        session.EndedAt.Should().BeNull();
    }

    [Fact]
    public void ConversationSession_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var startedAt = DateTime.UtcNow;
        var endedAt = DateTime.UtcNow.AddHours(1);

        // Act
        var session = new ConversationSession
        {
            Id = id,
            SessionName = "Test Session",
            StartedAt = startedAt,
            EndedAt = endedAt,
            ClientType = "claude-desktop",
            Metadata = new Dictionary<string, object> { ["version"] = "1.0" }
        };

        // Assert
        session.Id.Should().Be(id);
        session.SessionName.Should().Be("Test Session");
        session.StartedAt.Should().Be(startedAt);
        session.EndedAt.Should().Be(endedAt);
        session.ClientType.Should().Be("claude-desktop");
        session.Metadata!["version"].Should().Be("1.0");
    }

    #endregion

    #region UserPersona Tests

    [Fact]
    public void UserPersona_DefaultValues_AreCorrect()
    {
        // Act
        var persona = new UserPersona
        {
            AttributeType = null,
            AttributeKey = null,
            AttributeValue = null
        };

        // Assert
        persona.Id.Should().BeEmpty();
        persona.UserId.Should().BeNull();
        persona.AttributeType.Should().BeNull();
        persona.AttributeKey.Should().BeNull();
        persona.AttributeValue.Should().BeNull();
        persona.Confidence.Should().Be(0);
    }

    [Fact]
    public void UserPersona_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var memoryId = Guid.CreateVersion7();

        // Act
        var persona = new UserPersona
        {
            Id = id,
            UserId = "user123",
            AttributeType = "preferences",
            AttributeKey = "theme",
            AttributeValue = "dark",
            Confidence = 0.9f,
            SourceMemoryId = memoryId
        };

        // Assert
        persona.Id.Should().Be(id);
        persona.UserId.Should().Be("user123");
        persona.AttributeType.Should().Be("preferences");
        persona.AttributeKey.Should().Be("theme");
        persona.AttributeValue.Should().Be("dark");
        persona.Confidence.Should().Be(0.9f);
        persona.SourceMemoryId.Should().Be(memoryId);
    }

    #endregion

    #region Result Type Tests

    [Fact]
    public void SearchMode_HasCorrectValues()
    {
        // Assert
        SearchMode.Semantic.Should().Be(SearchMode.Semantic);
        SearchMode.Text.Should().Be(SearchMode.Text);
        SearchMode.Hybrid.Should().Be(SearchMode.Hybrid);
        Enum.GetValues<SearchMode>().Should().HaveCount(3);
    }

    [Fact]
    public void MemoryIngestResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new MemoryIngestResult();

        // Assert
        result.MemoryId.Should().BeEmpty();
        result.EntitiesCreated.Should().Be(0);
        result.RelationshipsCreated.Should().Be(0);
        result.Entities.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public void MemorySearchResult_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();

        // Act
        var result = new MemorySearchResult
        {
            Id = id,
            Content = "Test content",
            CreatedAt = DateTime.UtcNow,
            Source = "test-source",
            Similarity = 0.85f,
            Rank = 1.5f,
            Entities = new List<EntityInfo>
            {
                new() { Id = Guid.CreateVersion7(), Name = "John", Type = "PERSON", Confidence = 0.9f }
            }
        };

        // Assert
        result.Id.Should().Be(id);
        result.Content.Should().Be("Test content");
        result.Source.Should().Be("test-source");
        result.Similarity.Should().Be(0.85f);
        result.Rank.Should().Be(1.5f);
        result.Entities.Should().HaveCount(1);
    }

    [Fact]
    public void MultiHopSearchResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new MultiHopSearchResult();

        // Assert
        result.Hops.Should().Be(0);
        result.Memories.Should().BeEmpty();
        result.Entities.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public void EntityInfo_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.CreateVersion7();

        // Act
        var info = new EntityInfo
        {
            Id = id,
            Name = "Microsoft",
            Type = "ORG",
            Confidence = 0.95f
        };

        // Assert
        info.Id.Should().Be(id);
        info.Name.Should().Be("Microsoft");
        info.Type.Should().Be("ORG");
        info.Confidence.Should().Be(0.95f);
    }

    [Fact]
    public void RelationshipInfo_CanSetAllProperties()
    {
        // Act
        var info = new RelationshipInfo
        {
            Source = "John",
            Target = "Microsoft",
            Type = "WORKS_AT",
            Confidence = 0.9f
        };

        // Assert
        info.Source.Should().Be("John");
        info.Target.Should().Be("Microsoft");
        info.Type.Should().Be("WORKS_AT");
        info.Confidence.Should().Be(0.9f);
    }

    #endregion

    #region CORE Import Type Tests

    [Fact]
    public void CoreExportData_DefaultValues_AreCorrect()
    {
        // Act
        var data = new CoreExportData();

        // Assert
        data.Entities.Should().BeEmpty();
        data.Relations.Should().BeEmpty();
    }

    [Fact]
    public void CoreEntity_CanSetAllProperties()
    {
        // Act
        var entity = new CoreEntity
        {
            Name = "John",
            EntityType = "PERSON",
            Observations = new List<string> { "Works at Microsoft", "Lives in Seattle" }
        };

        // Assert
        entity.Name.Should().Be("John");
        entity.EntityType.Should().Be("PERSON");
        entity.Observations.Should().HaveCount(2);
        entity.Observations.Should().Contain("Works at Microsoft");
    }

    [Fact]
    public void CoreRelation_CanSetAllProperties()
    {
        // Act
        var relation = new CoreRelation
        {
            From = "John",
            To = "Microsoft",
            RelationType = "works_at"
        };

        // Assert
        relation.From.Should().Be("John");
        relation.To.Should().Be("Microsoft");
        relation.RelationType.Should().Be("works_at");
    }

    [Fact]
    public void CoreImportResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new CoreImportResult();

        // Assert
        result.EntitiesImported.Should().Be(0);
        result.RelationsImported.Should().Be(0);
        result.ObservationsImported.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }

    #endregion
}
