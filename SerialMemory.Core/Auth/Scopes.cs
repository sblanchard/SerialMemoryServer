namespace SerialMemory.Core.Auth;

/// <summary>
/// OAuth2/JWT scopes for SerialMemory API access control.
/// </summary>
public static class Scopes
{
    /// <summary>
    /// Core memory operations: search, ingest, user persona, sessions.
    /// Required for: SerialMemory.Mcp.Core
    /// </summary>
    public const string Core = "serialmemory.core";

    /// <summary>
    /// Administrative operations: lifecycle, safety, observability tools.
    /// Required for: SerialMemory.Mcp.Admin
    /// </summary>
    public const string Admin = "serialmemory.admin";

    /// <summary>
    /// Data export operations: workspace export, memory export, graph export.
    /// </summary>
    public const string Export = "serialmemory.export";

    /// <summary>
    /// Destructive operations: memory delete, tenant delete.
    /// Requires explicit user consent.
    /// </summary>
    public const string Delete = "serialmemory.delete";

    /// <summary>
    /// Read-only access to memories and entities.
    /// </summary>
    public const string Read = "serialmemory.read";

    /// <summary>
    /// Write access to memories and entities.
    /// </summary>
    public const string Write = "serialmemory.write";

    /// <summary>
    /// All available scopes.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Core,
        Admin,
        Export,
        Delete,
        Read,
        Write
    };

    /// <summary>
    /// Default scopes for free tier.
    /// </summary>
    public static readonly IReadOnlySet<string> FreeTier = new HashSet<string>
    {
        Core,
        Read,
        Write
    };

    /// <summary>
    /// Scopes included in Pro tier.
    /// </summary>
    public static readonly IReadOnlySet<string> ProTier = new HashSet<string>
    {
        Core,
        Export,
        Read,
        Write
    };

    /// <summary>
    /// Scopes included in Enterprise tier.
    /// </summary>
    public static readonly IReadOnlySet<string> EnterpriseTier = new HashSet<string>
    {
        Core,
        Admin,
        Export,
        Delete,
        Read,
        Write
    };
}
