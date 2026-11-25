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
    public bool? EnablePerformanceSampling { get; set; }
    public bool? EnableApiExplorer { get; set; }
    public WebDashboardUpdate? WebDashboard { get; set; }
    public ProxySecurityUpdate? ProxySecurity { get; set; }
}

/// <summary>Partial web-dashboard settings update. The certificate password is write-only.</summary>
internal sealed class WebDashboardUpdate
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public string? ListenAddress { get; set; }
    public string? Scheme { get; set; }
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
    public string? DashboardRole { get; set; }
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
/// API key create/update payload. On create the server generates the key material; on update
/// only label and allowed-model list are mutable. Empty/absent AllowedModels on create means
/// "all models"; on update null leaves the list untouched.
/// </summary>
internal sealed class KeyWrite
{
    public string? Label { get; set; }
    public List<string>? AllowedModels { get; set; }
}