namespace Kaeo.LlmProxy.Modules;

/// <summary>
/// Services and host information handed to a module during <see cref="IKaeoModule.Initialize"/>.
/// </summary>
public sealed class ModuleContext
{
    public ModuleContext(IModuleDatabase database, ISecretProvider secrets, HostInfo host, IMcpActivityLog activityLog, string dataDirectory)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        ActivityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    /// <summary>Gateway to the shared application database for schema and module data access.</summary>
    public IModuleDatabase Database { get; }

    /// <summary>Read-only access to the host's central credential store.</summary>
    public ISecretProvider Secrets { get; }

    /// <summary>Information about the host application and its endpoints.</summary>
    public HostInfo Host { get; }

    /// <summary>
    /// Sink for recording module activity into the host's MCP request log (Logs tab, MCP
    /// sub-tab). Entries written here are persisted alongside the MCP server's HTTP rows.
    /// </summary>
    public IMcpActivityLog ActivityLog { get; }

    /// <summary>
    /// Directory the host application uses to store its database and other persistent data.
    /// Modules may place auxiliary files (module-owned databases, git mirrors) here.
    /// </summary>
    public string DataDirectory { get; }
}

/// <summary>
/// Read-only snapshot of host endpoint information useful to modules (e.g. for linking the
/// host's API documentation into the module's own UI).
/// </summary>
public sealed class HostInfo
{
    public HostInfo(string listenAddress, int listenPort, bool apiExplorerEnabled, string? openApiSpecUrl)
    {
        ListenAddress = listenAddress;
        ListenPort = listenPort;
        ApiExplorerEnabled = apiExplorerEnabled;
        OpenApiSpecUrl = openApiSpecUrl;
    }

    /// <summary>Address the host proxy is configured to listen on (e.g. "localhost", "0.0.0.0").</summary>
    public string ListenAddress { get; }

    /// <summary>Port the host proxy is configured to listen on.</summary>
    public int ListenPort { get; }

    /// <summary>Whether the host's API explorer is enabled.</summary>
    public bool ApiExplorerEnabled { get; }

    /// <summary>
    /// Absolute URL of the host's OpenAPI document when the explorer is enabled; otherwise null.
    /// </summary>
    public string? OpenApiSpecUrl { get; }

    /// <summary>
    /// Display-friendly host name for building client-facing URLs. Wildcard bind addresses
    /// resolve to "localhost" since a browser cannot navigate to 0.0.0.0.
    /// </summary>
    public string DisplayHost
    {
        get
        {
            string host = ListenAddress.Trim();
            if (host.Length == 0 || host is "0.0.0.0" or "+" or "[::]" or "::")
                return "localhost";
            return host;
        }
    }
}
