using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.Core.Models;

/// <summary>The upstream API compatibility this mapping targets.</summary>
internal enum UpstreamType
{
    /// <summary>Legacy persisted value. Treat as OpenAI-compatible.</summary>
    LlamaCpp,

    /// <summary>OpenAI-compatible /v1 API, including llama.cpp server and hosted OpenAI-style services.</summary>
    OpenAI,
}

internal static class UpstreamTypeExtensions
{
    public static string ToDisplayName(this UpstreamType upstreamType) => upstreamType switch
    {
        UpstreamType.LlamaCpp or UpstreamType.OpenAI => "OpenAI Compatible",
        _ => upstreamType.ToString(),
    };

    public static UpstreamType FromDisplayName(string? displayName)
    {
        if (string.Equals(displayName, "OpenAI Compatible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(displayName, "OpenAI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(displayName, "LlamaCpp", StringComparison.OrdinalIgnoreCase))
        {
            return UpstreamType.OpenAI;
        }

        return Enum.TryParse(displayName, out UpstreamType parsed)
            ? parsed
            : UpstreamType.OpenAI;
    }
}

/// <summary>Named custom instruction set that can be injected into AI requests.</summary>
internal sealed class InstructionSet
{
    /// <summary>Unique name for this instruction set.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The instruction text to inject into requests.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Optional description for this instruction set.</summary>
    public string? Description { get; set; }
}

/// <summary>Mutable runtime settings stored in the application database.</summary>
internal sealed class RuntimeSettings
{
    public bool AutoStartProxy { get; set; } = true;

    public bool StartWithDashboardOpen { get; set; } = false;

    public bool AllowMultipleInstances { get; set; } = false;

    public bool ShowCloseToTrayNotification { get; set; } = true;

    public bool CollectRequestDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    public bool CollectResponseDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    public bool EnableStreamingHeartbeats { get; set; } = true;

    public int StreamingHeartbeatIntervalSeconds { get; set; } = 15;
}

/// <summary>Maps an externally exposed proxy model name to a specific upstream server and model name.</summary>
internal sealed class ModelMapping
{
    /// <summary>When false, this mapping is hidden from discovery and ignored for request routing.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The model name as exposed by this proxy to clients (e.g. "llama3").</summary>
    [JsonPropertyName("OllamaName")]
    public string ProxyName { get; set; } = string.Empty;

    /// <summary>The actual model name to request from the upstream server (e.g. "llama-3-8b").</summary>
    [JsonPropertyName("LlamaCppName")]
    public string ModelName { get; set; } = string.Empty;

    public bool EnableThinkingCompatibility { get; set; } = true;

    /// <summary>
    /// When true, this mapping participates in streaming heartbeat emission while waiting for upstream tokens.
    /// The global <see cref="AppSettings.EnableStreamingHeartbeats"/> must also be enabled. Default: true.
    /// </summary>
    public bool EnableHeartbeats { get; set; } = true;

    /// <summary>Upstream API compatibility for this mapping. Defaults to OpenAI-compatible /v1.</summary>
    public UpstreamType UpstreamType { get; set; } = UpstreamType.OpenAI;

    /// <summary>
    /// Optional bearer API key used when forwarding requests to OpenAI-compatible online services.
    /// Leave empty for local upstreams that do not require authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Upstream base URL for this mapping (e.g. "http://192.168.1.10:8080"). Required.
    /// Each mapping must specify its own upstream server.
    /// </summary>
    public string UpstreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout in seconds for this mapping. Default: 300 seconds if not specified or zero.
    /// </summary>
    public int UpstreamTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Repeat penalty to send to compatible upstreams. 1.0 is neutral/no penalty.
    /// </summary>
    public double RepeatPenalty { get; set; } = 1.0;

    /// <summary>
    /// Default temperature to use for this model in the Test Console. Upstream proxy requests keep their client-supplied value.
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Enable automatic context summarization when the model's context window is exceeded.
    /// When enabled, the proxy will automatically summarize older conversation history
    /// and retry the request with condensed context. Default: true.
    /// </summary>
    public bool EnableAutoSummarization { get; set; } = true;

    /// <summary>
    /// Number of recent message exchanges to preserve when summarizing context.
    /// Older messages will be summarized into a single condensed message.
    /// One exchange = user message + assistant response. Min: 2, Max: 20. Default: 4.
    /// </summary>
    public int PreserveRecentMessageCount { get; set; } = 4;

    /// <summary>
    /// Maximum number of times to retry with summarization on context overflow.
    /// Prevents infinite retry loops. Min: 1, Max: 3. Default: 2.
    /// </summary>
    public int MaxSummarizationRetries { get; set; } = 2;

    /// <summary>
    /// Optional name of the instruction set to inject into requests for this model.
    /// When specified, the instructions will be prepended to the conversation.
    /// </summary>
    public string? InstructionSetName { get; set; }

    /// <summary>
    /// When true, captured request bodies for this model are replaced with a redaction marker.
    /// Global CollectRequestDetails must also be enabled for any request body to be stored.
    /// </summary>
    public bool RedactRequestBodies { get; set; } = true;

    /// <summary>
    /// When true, captured response bodies for this model are replaced with a redaction marker.
    /// Global CollectResponseDetails must also be enabled for any response body to be stored.
    /// </summary>
    public bool RedactResponseBodies { get; set; } = true;

    /// <summary>
    /// When true, known sensitive JSON fields such as authorization, api keys, prompts, and messages are redacted.
    /// Applies when body-level redaction is disabled but detail capture is enabled.
    /// </summary>
    public bool RedactSensitiveJsonFields { get; set; } = true;
}

/// <summary>Logging configuration persisted inside settings.jsonc.</summary>
internal sealed class LoggingSettings
{
    /// <summary>Minimum Serilog level: Verbose, Debug, Information, Warning, Error, Fatal. Min: Verbose, Max: Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Maximum size in MB of a single app-log file before rolling. Min: 1, Max: 1000.</summary>
    public int AppLogFileSizeLimitMb { get; set; } = 10;

    /// <summary>Number of rolled app-log files to retain (oldest deleted first). Min: 1, Max: 999.</summary>
    public int AppLogRetainedFileCount { get; set; } = 7;

    /// <summary>
    /// Legacy database size setting retained for backward-compatible settings deserialization.
    /// Request-log growth is now controlled by retention cleanup instead of database-file archiving.
    /// </summary>
    public int RequestLogFileSizeLimitMb { get; set; } = 50;

    /// <summary>
    /// Full path of the active SQLite application database file. Empty uses the default path under the application Data directory.
    /// </summary>
    public string ApplicationDatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// How long to retain request log entries before they are automatically deleted.
    /// Set to 0 to keep entries forever. Default: 72 hours (3 days).
    /// </summary>
    public int LogRetentionHours { get; set; } = 72;

    /// <summary>Root directory for all log output. Relative to executable directory for portable deployment.</summary>
    public string LogDirectory { get; set; } = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Data", "logs");

    public string GetApplicationDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(ApplicationDatabasePath))
            return ApplicationDatabasePath;

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Database", "kaeo_llm_proxy.db");
    }
}

/// <summary>Persisted application settings.</summary>
internal sealed class AppSettings
{
    private static readonly string _settingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Data",
        "settings.jsonc");

    // Read: allow // and /* */ comments so the annotated template remains valid.
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    // Write: indented JSON used when serialising back (comments stripped, that is fine).
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Port this proxy listens on (Ollama default: 11434). Min: 1, Max: 65535.</summary>
    public int ListenPort { get; set; } = 11434;

    /// <summary>
    /// IP address to bind the listener to. Use "localhost" (127.0.0.1), "0.0.0.0" (all interfaces), 
    /// or a specific IP address. Note: Binding to "0.0.0.0" or specific IPs may require admin rights 
    /// or netsh urlacl reservation. Default: "localhost".
    /// </summary>
    public string ListenAddress { get; set; } = "localhost";

    /// <summary>Model name mappings loaded from the application database at startup.</summary>
    [JsonIgnore]
    public List<ModelMapping> ModelMappings { get; set; } = [];

    /// <summary>Named instruction sets loaded from the application database at startup.</summary>
    [JsonIgnore]
    public List<InstructionSet> InstructionSets { get; set; } = [];

    /// <summary>Maximum number of log entries to keep in memory. Min: 10, Max: 100000.</summary>
    public int MaxLogEntries { get; set; } = 500;

    /// <summary>Automatically start the proxy when the application launches. Default: true.</summary>
    [JsonIgnore]
    public bool AutoStartProxy { get; set; } = true;

    /// <summary>Open the dashboard window on startup instead of starting minimised to tray. Default: false.</summary>
    [JsonIgnore]
    public bool StartWithDashboardOpen { get; set; } = false;

    /// <summary>
    /// When true, allows more than one instance of the application to run simultaneously.
    /// By default only a single instance is permitted; attempting to launch a second instance
    /// will display a message and exit. Advanced users may set this to true when running
    /// multiple proxy configurations side-by-side. Default: false.
    /// </summary>
    [JsonIgnore]
    public bool AllowMultipleInstances { get; set; } = false;

    /// <summary>
    /// When true, show a notification dialog the first time the main window is closed to the tray.
    /// Users can disable it from that dialog. Default: true.
    /// </summary>
    [JsonIgnore]
    public bool ShowCloseToTrayNotification { get; set; } = true;

    /// <summary>
    /// When true, the raw request body is captured into each <see cref="RequestLog"/> entry.
    /// Useful for debugging but increases memory and storage usage. Default: false.
    /// </summary>
    [JsonIgnore]
    public bool CollectRequestDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// When true, the assembled LLM response text is captured into each <see cref="RequestLog"/> entry.
    /// For streaming responses this accumulates all chunks into a single string.
    /// Useful for debugging but increases memory and storage usage. Default: false.
    /// </summary>
    [JsonIgnore]
    public bool CollectResponseDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// When true, streaming responses emit harmless heartbeat frames while waiting for long-thinking models.
    /// Helps clients keep connections open when no model tokens are available yet. Default: true.
    /// </summary>
    [JsonIgnore]
    public bool EnableStreamingHeartbeats { get; set; } = true;

    /// <summary>
    /// Seconds between streaming heartbeat frames while waiting for upstream tokens. Min: 5, Max: 300. Default: 15.
    /// </summary>
    [JsonIgnore]
    public int StreamingHeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>Logging configuration.</summary>
    public LoggingSettings Logging { get; set; } = new();

    public static AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            AppSettings defaults = new();
            defaults.CreateDefaultFile();
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _readOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        string dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, _writeOptions));
    }

    public RuntimeSettings CreateRuntimeSettings() => new()
    {
        AutoStartProxy = AutoStartProxy,
        StartWithDashboardOpen = StartWithDashboardOpen,
        AllowMultipleInstances = AllowMultipleInstances,
        ShowCloseToTrayNotification = ShowCloseToTrayNotification,
        CollectRequestDetails = CollectRequestDetails,
        CollectResponseDetails = CollectResponseDetails,
        EnableStreamingHeartbeats = EnableStreamingHeartbeats,
        StreamingHeartbeatIntervalSeconds = StreamingHeartbeatIntervalSeconds,
    };

    public void ApplyRuntimeSettings(RuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        AutoStartProxy = runtimeSettings.AutoStartProxy;
        StartWithDashboardOpen = runtimeSettings.StartWithDashboardOpen;
        AllowMultipleInstances = runtimeSettings.AllowMultipleInstances;
        ShowCloseToTrayNotification = runtimeSettings.ShowCloseToTrayNotification;
        CollectRequestDetails = runtimeSettings.CollectRequestDetails;
        CollectResponseDetails = runtimeSettings.CollectResponseDetails;
        EnableStreamingHeartbeats = runtimeSettings.EnableStreamingHeartbeats;
        StreamingHeartbeatIntervalSeconds = runtimeSettings.StreamingHeartbeatIntervalSeconds;
    }

    /// <summary>
    /// Resolves a requested model name to the upstream model name.
    /// Returns the mapped upstream name if an enabled mapping is found, otherwise returns the original name unchanged.
    /// </summary>
    public string ResolveModelName(string requestedModel)
    {
        foreach (ModelMapping mapping in ModelMappings)
        {
            if (mapping.IsEnabled && string.Equals(mapping.ProxyName, requestedModel, StringComparison.OrdinalIgnoreCase))
                return mapping.ModelName;
        }

        return requestedModel;
    }

    /// <summary>
    /// Finds an enabled model mapping by either the exposed proxy name or the upstream model name.
    /// Returns null when no enabled configured mapping matches.
    /// </summary>
    public ModelMapping? FindModelMapping(string requestedModel)
    {
        foreach (ModelMapping mapping in ModelMappings)
        {
            if (!mapping.IsEnabled)
                continue;

            if (string.Equals(mapping.ProxyName, requestedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mapping.ModelName, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds an instruction set by name (case-insensitive).
    /// Returns null when no instruction set with the given name exists.
    /// </summary>
    public InstructionSet? FindInstructionSet(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (InstructionSet instructionSet in InstructionSets)
        {
            if (string.Equals(instructionSet.Name, name, StringComparison.OrdinalIgnoreCase))
                return instructionSet;
        }

        return null;
    }

    /// <summary>Writes the annotated default config template to disk on first run.</summary>
    private void CreateDefaultFile()
    {
        string dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);

        string logDir = Logging.LogDirectory.Replace("\\", "\\\\");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // \u2500\u2500\u2500 Proxy \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        sb.AppendLine();
        sb.AppendLine("  // Port the proxy listens on (Ollama clients connect here).");
        sb.AppendLine("  // Min: 1  Max: 65535  Default: 11434");
        sb.AppendLine($"  \"ListenPort\": {ListenPort},");
        sb.AppendLine();
        sb.AppendLine("  // IP address to bind the listener to.");
        sb.AppendLine("  // Values: \"localhost\" (127.0.0.1 only), \"0.0.0.0\" (all interfaces), or a specific IP.");
        sb.AppendLine("  // Note: Binding to \"0.0.0.0\" or specific IPs may require admin rights or netsh urlacl.");
        sb.AppendLine("  // Default: \"localhost\"");
        sb.AppendLine($"  \"ListenAddress\": \"{ListenAddress}\",");

        sb.AppendLine();
        sb.AppendLine("  // Max recent request log entries kept in memory for the GUI.");
        sb.AppendLine("  // Min: 10  Max: 100000  Default: 500");
        sb.AppendLine($"  \"MaxLogEntries\": {MaxLogEntries},");
        sb.AppendLine();
        sb.AppendLine("  // \u2500\u2500\u2500 Logging \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        sb.AppendLine("  \"Logging\": {");
        sb.AppendLine();
        sb.AppendLine("    // Minimum Serilog severity level written to the app log.");
        sb.AppendLine("    // Values: Verbose | Debug | Information | Warning | Error | Fatal");
        sb.AppendLine($"    \"MinimumLevel\": \"{Logging.MinimumLevel}\",");
        sb.AppendLine();
        sb.AppendLine("    // Roll the app log file when it reaches this size (MB).");
        sb.AppendLine("    // Min: 1  Max: 1000  Default: 10");
        sb.AppendLine($"    \"AppLogFileSizeLimitMb\": {Logging.AppLogFileSizeLimitMb},");
        sb.AppendLine();
        sb.AppendLine("    // How many rolled app log files to keep before deleting the oldest.");
        sb.AppendLine("    // Min: 1  Max: 999  Default: 7");
        sb.AppendLine($"    \"AppLogRetainedFileCount\": {Logging.AppLogRetainedFileCount},");
        sb.AppendLine();
        sb.AppendLine("    // Archive the LiteDB application database when it reaches this size (MB).");
        sb.AppendLine("    // Min: 1  Max: 5000  Default: 50");
        sb.AppendLine($"    \"RequestLogFileSizeLimitMb\": {Logging.RequestLogFileSizeLimitMb},");
        sb.AppendLine();
        sb.AppendLine("    // Full path to the central LiteDB application database file.");
        sb.AppendLine("    // Empty uses Data/Database/kaeo_llm_proxy.db under the application directory.");
        sb.AppendLine($"    \"ApplicationDatabasePath\": \"{Logging.ApplicationDatabasePath.Replace("\\", "\\\\")}\",");
        sb.AppendLine();
        sb.AppendLine("    // Root directory for text log files.");
        sb.AppendLine($"    \"LogDirectory\": \"{logDir}\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        File.WriteAllText(_settingsPath, sb.ToString());
    }
}
