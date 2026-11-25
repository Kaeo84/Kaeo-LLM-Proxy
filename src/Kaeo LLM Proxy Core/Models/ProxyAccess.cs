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
/// An API key belonging to a <see cref="ProxyUser"/>. Only the SHA-256 hash of the full key is
/// stored; the plaintext is shown exactly once at creation. <see cref="KeyPrefix"/> is a short
/// non-secret fragment so the dashboard can tell keys apart.
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

    /// <summary>When the key was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Last time the key authenticated a request (UTC); null when never used.</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>
    /// Proxy model names this key may call. Empty means every configured model. Matched
    /// case-insensitively against the model name the client requests.
    /// </summary>
    public List<string> AllowedModels { get; set; } = [];
}