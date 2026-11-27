namespace Kaeo.LlmProxy.Web;

/// <summary>Write-only credential payload. Secrets are accepted but never returned by any GET.</summary>
internal sealed class CredentialWrite
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string? PrivateKey { get; set; }
    public string? Certificate { get; set; }
}

/// <summary>Module enable/disable request body.</summary>
internal sealed class ModuleToggle
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Partial settings update. Only non-null fields are applied; everything else is left untouched.
/// Deliberately excludes passphrase, credentials, and other secret material.
/// </summary>
internal sealed class SettingsUpdate
{
    public int? ListenPort { get; set; }
    public string? ListenAddress { get; set; }
    public bool? AutoStartProxy { get; set; }
    public int? MaxConcurrentRequests { get; set; }
    public long? MaxRequestBodyBytes { get; set; }
    public int? MaxLogEntries { get; set; }
    public bool? EnableCors { get; set; }
    public bool? CollectRequestDetails { get; set; }
    public bool? CollectResponseDetails { get; set; }
    public bool? DebugMode { get; set; }
    public bool? HideHeartbeatLogs { get; set; }
    public bool? EnableSseKeepAlive { get; set; }
    public int? SseKeepAliveIntervalSeconds { get; set; }
    public int? HeartbeatIntervalSeconds { get; set; }
    public bool? EnableModelAutoDiscovery { get; set; }
    public int? ModelAutoDiscoveryIntervalSeconds { get; set; }
    public int? ModelAutoDiscoveryOfflineMinutes { get; set; }
    public bool? EnablePerformanceSampling { get; set; }
    public bool? EnableApiExplorer { get; set; }
    public bool? EnableRegistrationApiSpec { get; set; }
    public bool? AllowMultipleInstances { get; set; }
    public WebDashboardUpdate? WebDashboard { get; set; }
    public ProxySecurityUpdate? ProxySecurity { get; set; }
}

/// <summary>Partial web-dashboard settings update. The certificate password is write-only.</summary>
internal sealed class WebDashboardUpdate
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public string? ListenAddress { get; set; }
    public bool? RequireLogin { get; set; }
    public string? Scheme { get; set; }

    /// <summary>
    /// Name of a stored credential holding the HTTPS certificate. Preferred over
    /// <see cref="CertificatePath"/>; sending an empty string clears it.
    /// </summary>
    public string? CertificateCredentialName { get; set; }

    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}

/// <summary>Partial proxy-security settings update.</summary>
internal sealed class ProxySecurityUpdate
{
    public bool? RequireApiKey { get; set; }
}

/// <summary>Proxy user create/update payload. Null fields on update are left untouched.</summary>
internal sealed class UserWrite
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public bool? IsEnabled { get; set; }
    public bool? DashboardLoginEnabled { get; set; }
    public string? DashboardUsername { get; set; }
    public string? DashboardPassword { get; set; }

    /// <summary>Legacy single-role field; still accepted so older clients keep working.</summary>
    public string? DashboardRole { get; set; }

    /// <summary>Every role the user holds. Replaces <see cref="DashboardRole"/> when supplied.</summary>
    public List<string>? DashboardRoles { get; set; }

    /// <summary>Models this user may call with any of their keys. Null leaves the list untouched.</summary>
    public List<string>? AllowedModels { get; set; }

    /// <summary>Whether this user may use the MCP server. Null leaves the grant untouched.</summary>
    public bool? McpAccess { get; set; }

    /// <summary>
    /// Which MCP modules this user may reach, identified by module registry id (or name when the
    /// module reports no id). An empty list means every module; null leaves the list untouched.
    /// </summary>
    public List<string>? AllowedMcpModules { get; set; }
}

internal sealed class DashboardLoginWrite
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>First-run setup payload: creates the initial administrator dashboard account.</summary>
internal sealed class DashboardSetupWrite
{
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// API key create/update payload. On create the server generates the key material; on update only
/// the label is mutable. Model and MCP access belong to the user, not the key, so every key a user
/// holds inherits their grants.
/// </summary>
internal sealed class KeyWrite
{
    public string? Label { get; set; }
}

/// <summary>
/// Test Console prompt. Sampling values are optional: when omitted the model's own configuration
/// is left to the upstream, and a model configured to defer to the provider ignores them anyway.
/// </summary>
internal sealed class TestChatWrite
{
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public string? System { get; set; }
    public double? Temperature { get; set; }
    public double? RepeatPenalty { get; set; }
}

/// <summary>
/// One live or recently completed streaming response. The rolling tail text is only populated by
/// <c>/api/streams/detail</c>; it stays null (and is therefore omitted from the JSON) in the list
/// response so a 2-second poll does not ship up to 64 KB per stream.
/// </summary>
internal sealed class StreamView
{
    public string RequestId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime LastUpstreamActivityUtc { get; set; }
    public DateTime LastClientActivityUtc { get; set; }
    public long UpstreamBytes { get; set; }
    public long ClientBytes { get; set; }
    public long KeepAliveFrames { get; set; }
    public int FrameCount { get; set; }
    public bool IsLive { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string? RawUpstreamText { get; set; }
    public string? ForwardedText { get; set; }
}