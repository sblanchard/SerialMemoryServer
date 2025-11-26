namespace SerialMemory.Core.Auth;

/// <summary>
/// User roles within a tenant for authorization decisions.
/// </summary>
public static class Roles
{
    /// <summary>
    /// Tenant owner - full control including deletion and billing.
    /// </summary>
    public const string Owner = "owner";

    /// <summary>
    /// Administrator - can manage users, settings, and run admin tools.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Member - can read and write memories, entities.
    /// </summary>
    public const string Member = "member";

    /// <summary>
    /// Read-only - can only read data, no write access.
    /// </summary>
    public const string ReadOnly = "readonly";

    /// <summary>
    /// Service account - for automated processes and integrations.
    /// </summary>
    public const string Service = "service";

    /// <summary>
    /// Roles that have administrative privileges.
    /// </summary>
    public static readonly IReadOnlySet<string> AdminRoles = new HashSet<string>
    {
        Owner,
        Admin
    };

    /// <summary>
    /// Roles that can write data.
    /// </summary>
    public static readonly IReadOnlySet<string> WriteRoles = new HashSet<string>
    {
        Owner,
        Admin,
        Member,
        Service
    };

    /// <summary>
    /// Roles that can only read data.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnlyRoles = new HashSet<string>
    {
        ReadOnly
    };

    /// <summary>
    /// All valid roles.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Owner,
        Admin,
        Member,
        ReadOnly,
        Service
    };

    /// <summary>
    /// Checks if the given role has admin privileges.
    /// </summary>
    public static bool IsAdmin(string? role) =>
        role != null && AdminRoles.Contains(role);

    /// <summary>
    /// Checks if the given role can write data.
    /// </summary>
    public static bool CanWrite(string? role) =>
        role != null && WriteRoles.Contains(role);

    /// <summary>
    /// Checks if the given role is valid.
    /// </summary>
    public static bool IsValid(string? role) =>
        role != null && All.Contains(role);
}
