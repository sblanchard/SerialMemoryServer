namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for LLM chat completions.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Provider name (e.g., "OpenAI", "Ollama")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Model name (e.g., "gpt-5-nano-2025-08-07", "qwen2.5:7b")
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// Simple chat completion.
    /// </summary>
    Task<string> ChatAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming chat completion.
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        CancellationToken cancellationToken = default);
}
