namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// A person or service represented in the application database. Model and MCP access are granted
/// to the person, not to an individual key, so every key they hold inherits the same reach;
/// optional dashboard credentials separately grant one or more dashboard roles.
/// </summary>
internal sealed class ProxyUser
{
    /// <summary>Database row id.</summary>
    public int Id { get; set; }

    /// <summary>Display name (required).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contact email address (optional but recommended).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>When the user was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    public bool DashboardLoginEnabled { get; set; }

    public string DashboardUsername { get; set; } = string.Empty;

    public string DashboardPasswordHash { get; set; } = string.Empty;

        /// <summary>
        /// Comma-separated <see cref="DashboardRoles"/> keys ("userAdmin,readOnly"). A user may hold
        /// several roles and the effective capabilities are the union. Admin implies everything.
        /// </summary>
        public string DashboardRole { get; set; } = "readOnly";

        /// <summary>
        /// Proxy model names this user may call. Every key they hold inherits this list; Admin users
        /// bypass it. Empty means no model access (unless the user is an Admin).
        /// </summary>
        public List<string> AllowedModels { get; set; } = [];

        /// <summary>Whether this user may use the MCP server. Admin users bypass this flag.</summary>
        public bool McpAccess { get; set; }

        /// <summary>
        /// Disabled users keep their record and keys but every key authenticates as rejected,
        /// so access can be suspended without destroying the key material clients still hold.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>True when any of this user's dashboard roles is Admin.</summary>
        public bool IsDashboardAdmin => DashboardRoles.HasRole(DashboardRole, DashboardRoles.Admin);
    }

/// <summary>
/// An API key belonging to a <see cref="ProxyUser"/>. Requests authenticate against the SHA-256
/// hash in <see cref="KeyHash"/>; <see cref="KeySecret"/> additionally keeps the plaintext under
/// reversible encryption so an administrator can reveal or regenerate the key later.
/// <see cref="KeyPrefix"/> is a short non-secret fragment so the dashboard can tell keys apart.
/// </summary>
internal sealed class ProxyApiKey
{
    /// <summary>Database row id.</summary>
    public int Id { get; set; }

    /// <summary>Owning user's row id.</summary>
    public int UserId { get; set; }

    /// <summary>Friendly label ("laptop", "CI pipeline", …).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the full plaintext key. Never returned by any API.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First characters of the key (non-secret) for display/identification.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// The full plaintext key under reversible AES-GCM encryption (see <c>ApiKeyProtector</c>),
    /// so an administrator can reveal or replace it from the dashboard. Null for keys created
    /// before this was stored, or when the protection key file was unavailable at creation time;
    /// such keys must be regenerated before they can be read back.
    /// </summary>
    public string? KeySecret { get; set; }

    /// <summary>When the key was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Last time the key authenticated a request (UTC); null when never used.</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>
    /// Legacy per-key model grants. Superseded by <see cref="ProxyUser.AllowedModels"/>: the
    /// database keeps the column so pre-existing databases still load, and the startup migration
    /// folds any grants found here into the owning user. Nothing reads it for authorization.
    /// </summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>
    /// Legacy per-key MCP grant, superseded by <see cref="ProxyUser.McpAccess"/> for the same
    /// reason as <see cref="AllowedModels"/>.
    /// </summary>
    public bool McpAccess { get; set; }
}