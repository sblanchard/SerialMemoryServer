namespace SerialMemory.Core.Models;

/// <summary>
/// Stores user preferences, skills, goals, and background information
/// </summary>
public class UserPersona
{
    public Guid Id { get; set; }
    public string UserId { get; set; } 
    public required string AttributeType { get; set; } // preference, skill, goal, background
    public required string AttributeKey { get; set; }
    public required string AttributeValue { get; set; }
    public float Confidence { get; set; } = 0.0f;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? SourceMemoryId { get; set; }
}
