namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// A person or service represented in the application database. Proxy model access uses their
/// <see cref="ProxyApiKey"/>s; optional dashboard credentials separately grant a dashboard role.
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

    public string DashboardRole { get; set; } = "readOnly";

    /// <summary>
    /// Disabled users keep their record and keys but every key authenticates as rejected,
    /// so access can be suspended without destroying the key material clients still hold.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
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
    /// Proxy model names this key may call. Read Only users must have an explicit grant; Admin
    /// users bypass this list.
    /// </summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>Whether this key may use the MCP server. Admin users bypass this flag.</summary>
    public bool McpAccess { get; set; }
}