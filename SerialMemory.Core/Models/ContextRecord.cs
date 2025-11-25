namespace SerialMemory.Core.Models;

public record ContextRecord(
    string Key,
    string Data,
    DateTime UpdatedAtUtc);