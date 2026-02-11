namespace SerialMemory.Mcp;

/// <summary>
/// Encapsulates mutable session state for the MCP server.
/// Centralizes state that was previously scattered across top-level variables in Program.cs.
/// </summary>
internal sealed class McpSessionState
{
    /// <summary>
    /// The currently active conversation session ID, if any.
    /// </summary>
    public Guid? CurrentSessionId { get; private set; }

    /// <summary>
    /// Sets the current session to the given ID (e.g., after session initialization).
    /// </summary>
    public void SetSession(Guid sessionId) => CurrentSessionId = sessionId;

    /// <summary>
    /// Clears the current session (e.g., after session end).
    /// </summary>
    public void ClearSession() => CurrentSessionId = null;

    /// <summary>
    /// Temporarily overrides the current session ID.
    /// Returns an IDisposable that restores the previous value when disposed.
    /// </summary>
    public SessionOverride OverrideSession(Guid overrideSessionId)
    {
        var saved = CurrentSessionId;
        CurrentSessionId = overrideSessionId;
        return new SessionOverride(this, saved);
    }

    /// <summary>
    /// Represents a temporary session override that restores the previous value on dispose.
    /// </summary>
    internal readonly struct SessionOverride : IDisposable
    {
        private readonly McpSessionState _state;
        private readonly Guid? _savedSessionId;

        internal SessionOverride(McpSessionState state, Guid? savedSessionId)
        {
            _state = state;
            _savedSessionId = savedSessionId;
        }

        public void Dispose() => _state.CurrentSessionId = _savedSessionId;
    }
}
