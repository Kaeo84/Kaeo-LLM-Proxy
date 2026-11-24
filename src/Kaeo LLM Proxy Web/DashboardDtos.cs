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
}

/// <summary>Partial web-dashboard settings update. ApiKey/CertificatePassword are write-only.</summary>
internal sealed class WebDashboardUpdate
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public string? ListenAddress { get; set; }
    public bool? RequireApiKey { get; set; }
    public string? Scheme { get; set; }
    public string? CertificatePath { get; set; }
    public string? ApiKey { get; set; }
    public string? CertificatePassword { get; set; }
}