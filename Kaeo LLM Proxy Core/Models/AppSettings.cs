using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Kaeo.LlmProxy.Core.Models;

/// <summary>The upstream API compatibility this mapping targets.</summary>
internal enum UpstreamType
{
    /// <summary>Legacy persisted value. Treat as OpenAI-compatible.</summary>
    LlamaCpp,

    /// <summary>OpenAI-compatible /v1 API, including llama.cpp server and hosted OpenAI-style services.</summary>
    OpenAI,
}

/// <summary>
/// Determines which configured value wins for a sampling parameter (temperature / repeat penalty)
/// in upstream requests for a model mapping.
/// </summary>
internal enum SamplingPriority
{
    /// <summary>The client app's value wins and is passed through; omitted when the client sent none.</summary>
    ClientApp = 0,

    /// <summary>The proxy's per-model configured value always wins, overriding any client-supplied value.</summary>
    Proxy = 1,

    /// <summary>The field is omitted entirely so the provider's platform-configured value applies.</summary>
    Provider = 2,
}

/// <summary>
/// Wire formats the proxy uses when it injects a reasoning effort value into an upstream
/// request under <see cref="SamplingPriority.Proxy"/> priority, selectable in any combination.
/// Providers disagree on the shape: older OpenAI models read a top-level
/// <c>reasoning_effort</c> string, newer OpenAI models read a nested <c>reasoning.effort</c>
/// object, Qwen Cloud expects an <c>extra_body</c> wrapper carrying <c>enable_thinking</c>
/// alongside <c>reasoning_effort</c>, and local inference servers (llama.cpp, vLLM) read
/// <c>chat_template_kwargs.reasoning_effort</c>.
/// </summary>
[Flags]
internal enum ReasoningEffortFormat
{
    /// <summary>Legacy top-level <c>"reasoning_effort": "value"</c> property (e.g. o3-mini).</summary>
    Legacy = 1,

    /// <summary>Modern nested <c>"reasoning": { "enable": true, "thinking_level": "value" }</c> object.</summary>
    Modern = 2,

    /// <summary>Qwen Cloud style: <c>"extra_body": { "enable_thinking": true, "reasoning_effort": "value" }</c>.</summary>
    QwenCloud = 4,

    /// <summary>Local inference servers (llama.cpp, vLLM): <c>"chat_template_kwargs": { "reasoning_effort": "value" }</c>.</summary>
    ChatTemplateKwargs = 8,
}

/// <summary>
/// Controls how upstream reasoning/"thinking" text is transformed before being returned to clients.
/// </summary>
internal enum ThinkingMode
{
    /// <summary>
    /// Leave thinking text exactly where the upstream placed it (default). Inline
    /// <c>&lt;think&gt;...&lt;/think&gt;</c> blocks stay in the visible answer, and a native
    /// <c>reasoning_content</c> field is mirrored into <c>content</c> when the latter is empty.
    /// </summary>
    LeaveInline = 0,

    /// <summary>Legacy alias for <see cref="LeaveInline"/>.</summary>
    Off = 0,

    /// <summary>
    /// Extract <c>&lt;think&gt;...&lt;/think&gt;</c> blocks from the <c>content</c> field and
    /// re-emit them as <c>reasoning_content</c>, removing them from the visible answer. Used for
    /// providers such as Qwen Cloud that return reasoning inline in the older response format.
    /// </summary>
    ExtractThinkTags = 1,

    /// <summary>Alias for <see cref="ExtractThinkTags"/>.</summary>
    MoveToReasoningContent = 1,

    /// <summary>
    /// Remove <c>&lt;think&gt;...&lt;/think&gt;</c> blocks from the client-facing answer entirely
    /// without re-emitting them as <c>reasoning_content</c>. The unmodified upstream body (including
    /// the thinking text) is still available in captured request logs when response detail
    /// collection is enabled.
    /// </summary>
    StripFromOutput = 2,

    /// <summary>
    /// Qwen thinking compatibility. The model emits a literal <c>[Thinking]</c> marker at the start
    /// of its answer, followed by the reasoning, then a <c>[Answer]</c> marker and the final
    /// answer. The text between <c>[Thinking]</c> and <c>[Answer]</c> is re-emitted as
    /// <c>reasoning_content</c>, the text after <c>[Answer]</c> becomes the visible answer, and
    /// both literal markers are stripped from the client-facing output.
    /// </summary>
    QwenThinkingCompatible = 3,
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

/// <summary>
/// A named secret (e.g. an upstream API key) stored centrally so multiple model mappings can
/// reference it by name instead of each carrying its own copy. Secret material is kept in
/// plaintext in memory while the app runs but is encrypted at rest in the application database.
/// Besides a single <see cref="Secret"/> (API key / password), a credential may carry an
/// SSH-style <see cref="Username"/>, <see cref="PrivateKey"/>, and <see cref="Certificate"/>.
/// </summary>
internal sealed class StoredCredential
{
    /// <summary>Unique name for this credential, referenced by <see cref="ModelMapping.CredentialName"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The secret value (e.g. bearer API key or SSH password). Plaintext in memory, encrypted
    /// at rest. May be empty when the credential carries key/certificate material instead.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Optional description of what this credential is used for.</summary>
    public string? Description { get; set; }

    /// <summary>Optional username (e.g. an SSH login user) stored alongside the secret material.</summary>
    public string? Username { get; set; }

    /// <summary>Optional SSH private key (PEM or OpenSSH format). Plaintext in memory, encrypted at rest.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Optional SSH certificate paired with <see cref="PrivateKey"/>. Plaintext in memory, encrypted at rest.</summary>
    public string? Certificate { get; set; }

    /// <summary>Whether any secret material (secret, private key, or certificate) is present.</summary>
    public bool HasSecretMaterial =>
        !string.IsNullOrWhiteSpace(Secret)
        || !string.IsNullOrWhiteSpace(PrivateKey)
        || !string.IsNullOrWhiteSpace(Certificate);
}

/// <summary>Mutable runtime settings stored in the application database.</summary>
internal sealed class RuntimeSettings
{
    public bool AutoStartProxy { get; set; } = true;

    public bool StartWithDashboardOpen { get; set; } = false;

    public bool AllowMultipleInstances { get; set; } = false;

    /// <summary>
    /// When true, the application re-launches itself elevated (UAC prompt) at startup so http.sys
    /// accepts non-localhost listener bindings without a manual "Run as administrator".
    /// Ignored in debug builds. Default: false.
    /// </summary>
    public bool RunAsAdministrator { get; set; } = false;

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

    /// <summary>
    /// When true, each request log entry captures the proxy's before/after transformation:
    /// the raw upstream response body and a human-readable summary of every settings-driven
    /// override applied (temperature, repeat_penalty, instruction-set injection, reasoning
    /// effort, model rewrite). Independent of the Collect* detail flags. Default: false.
    /// </summary>
    public bool DebugMode { get; set; } = false;

    public bool EnableStreamingHeartbeats { get; set; } = true;

    public int StreamingHeartbeatIntervalSeconds { get; set; } = 15;

    public bool EnablePerformanceSampling { get; set; } = true;

    /// <summary>
    /// When true, the proxy serves a Scalar API explorer at /scalar and an OpenAPI
    /// specification at /openapi/v1/openapi.json. Default: false.
    /// </summary>
    public bool EnableApiExplorer { get; set; } = false;
}

/// <summary>Maps an externally exposed proxy model name to a specific upstream server and model name.</summary>
internal sealed class ModelMapping
{
    /// <summary>
    /// Thread-safe counter for generating unique mapping IDs. Initialized from the maximum ID
    /// seen when loading from the database, so new mappings always get IDs higher than any existing one.
    /// </summary>
    private static int _nextId;

    /// <summary>
    /// Stable unique identifier for this mapping. Assigned automatically when the mapping is created
    /// or loaded from the database. Never changes for the lifetime of a mapping, so cross-mapping
    /// references (e.g. <see cref="ContextSummarizeModelId"/>) survive proxy name renames.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Assigns the next available ID to this mapping if it doesn't already have one.
    /// Called by deserialization or database loading when Id is 0.
    /// </summary>
    internal void EnsureId()
    {
        if (Id == 0)
            Id = Interlocked.Increment(ref _nextId);
    }

    /// <summary>
    /// Updates the global ID counter to be at least as large as the given value.
    /// Called during database loading to ensure new mappings get IDs higher than any persisted one.
    /// </summary>
    internal static void TrackMaxId(int id)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _nextId);
            if (id <= current)
                return;
        } while (Interlocked.CompareExchange(ref _nextId, id, current) != current);
    }

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
    /// Capability tokens advertised for this model on the discovery endpoints (/api/tags and
    /// /v1/models), e.g. "text", "chat", "vision", "function_calling". Values are the canonical
    /// wire tokens from <see cref="ModelCapabilities"/> (see its <c>Normalize</c>); the list is
    /// emitted verbatim as the <c>capabilities</c> array. Explicit-only and default empty (opt-in) —
    /// there is no name-based inference, since upstream OpenAI-compatible /v1/models responses
    /// (e.g. Qwen Cloud) carry no metadata to infer capabilities from.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// When true, this mapping participates in streaming heartbeat emission while waiting for upstream tokens.
    /// The global <see cref="AppSettings.EnableStreamingHeartbeats"/> must also be enabled. Default: true.
    /// </summary>
    public bool EnableHeartbeats { get; set; } = true;

    /// <summary>Upstream API compatibility for this mapping. Defaults to OpenAI-compatible /v1.</summary>
    public UpstreamType UpstreamType { get; set; } = UpstreamType.OpenAI;

    /// <summary>
    /// Controls how upstream "thinking"/reasoning text is surfaced to clients. Some providers
    /// (e.g. Qwen Cloud's older response format) embed reasoning inside the normal <c>content</c>
    /// field wrapped in <c>&lt;think&gt;...&lt;/think&gt;</c> tags, which modern clients such as
    /// Visual Studio cannot roll into a dedicated thinking box. When set to
    /// <see cref="ThinkingMode.MoveToReasoningContent"/>, the proxy strips those tags out of
    /// <c>content</c> and re-emits the enclosed text as <c>reasoning_content</c> (for both
    /// streaming and non-streaming responses) so clients can render a collapsible thinking panel.
    /// <see cref="ThinkingMode.StripFromOutput"/> drops the thinking text from the client-facing
    /// answer entirely (it remains in captured logs). Defaults to
    /// <see cref="ThinkingMode.LeaveInline"/> (pass responses through unchanged).
    /// </summary>
    public ThinkingMode ThinkingMode { get; set; } = ThinkingMode.LeaveInline;

    /// <summary>
    /// Optional name of a centrally stored <see cref="StoredCredential"/> whose secret is used as
    /// the bearer API key for this mapping. Leave null for local upstreams that do not require
    /// authentication.
    /// </summary>
    public string? CredentialName { get; set; }

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
    /// Repeat penalty for compatible upstreams. 1.0 is neutral/no penalty. Used when
    /// <see cref="RepeatPenaltyPriority"/> is <see cref="SamplingPriority.Proxy"/> and as the
    /// Test Console default.
    /// </summary>
    public double RepeatPenalty { get; set; } = 1.0;

    /// <summary>
    /// Controls which temperature wins in upstream requests for this model.
    /// <see cref="SamplingPriority.ClientApp"/> passes the client's value through (omitted when
    /// the client sent none); <see cref="SamplingPriority.Proxy"/> always sends
    /// <see cref="Temperature"/>, overriding the client; <see cref="SamplingPriority.Provider"/>
    /// omits the field entirely so hosted providers keep their platform-configured value.
    /// Defaults to <see cref="SamplingPriority.ClientApp"/>.
    /// </summary>
    public SamplingPriority TemperaturePriority { get; set; } = SamplingPriority.ClientApp;

    /// <summary>
    /// Controls which repeat penalty wins in upstream requests for this model.
    /// <see cref="SamplingPriority.ClientApp"/> passes the client's value through (omitted when
    /// the client sent none); <see cref="SamplingPriority.Proxy"/> always sends
    /// <see cref="RepeatPenalty"/>, overriding the client; <see cref="SamplingPriority.Provider"/>
    /// omits the field entirely so hosted providers keep their platform default.
    /// Defaults to <see cref="SamplingPriority.ClientApp"/>.
    /// </summary>
    public SamplingPriority RepeatPenaltyPriority { get; set; } = SamplingPriority.ClientApp;

    /// <summary>
    /// Default temperature to use for this model in the Test Console. Upstream proxy requests keep their client-supplied value.
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Controls which reasoning_effort wins in upstream requests for this model.
    /// <see cref="SamplingPriority.ClientApp"/> passes the client's value through unchanged;
    /// <see cref="SamplingPriority.Proxy"/> always sends <see cref="ReasoningEffort"/>,
    /// overriding (or injecting for) the client; <see cref="SamplingPriority.Provider"/>
    /// omits the field entirely so hosted providers keep their platform default.
    /// Defaults to <see cref="SamplingPriority.ClientApp"/>.
    /// </summary>
    public SamplingPriority ReasoningEffortPriority { get; set; } = SamplingPriority.ClientApp;

    /// <summary>
    /// The reasoning_effort value sent to the upstream when <see cref="ReasoningEffortPriority"/>
    /// is <see cref="SamplingPriority.Proxy"/>. Null/empty sends nothing (falls back to pass-through).
    /// Should be one of <see cref="ReasoningEffortValues"/>.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Reasoning effort values this model supports, in priority order (highest priority first).
    /// Informational for upstreams that accept a list of available reasoning efforts; the value
    /// actually sent is controlled by <see cref="ReasoningEffortPriority"/> and
    /// <see cref="ReasoningEffort"/>. Empty disables reasoning effort handling.
    /// </summary>
    public List<string> ReasoningEffortValues { get; set; } = [];

    /// <summary>
    /// Wire formats emitted when the proxy injects <see cref="ReasoningEffort"/> under
    /// <see cref="SamplingPriority.Proxy"/> priority; any combination of flags may be selected
    /// and each selected format is written into the upstream request. Ignored for the other
    /// priorities: Client App passes the client's fields through unchanged and Provider omits
    /// them. Defaults to <see cref="ReasoningEffortFormat.Legacy"/>. Injected values are always
    /// lowercased because OpenAI-style providers expect lowercase effort levels.
    /// </summary>
    public ReasoningEffortFormat ReasoningEffortFormat { get; set; } = ReasoningEffortFormat.Legacy;

    /// <summary>
    /// Optional name of the instruction set to inject into requests for this model.
    /// When specified, the instructions will be prepended to the conversation.
    /// </summary>
    public string? InstructionSetName { get; set; }

    /// <summary>
    /// Optional ID of another proxy model to route context-summarize /compact requests to.
    /// When a request is detected as a Copilot /compact summary request (identified by its
    /// distinctive session-summary system prompt) and this mapping has a smaller/faster compact
    /// model configured, the request is transparently redirected to that model — its upstream,
    /// sampling, and instruction-set settings all apply. Leave null to handle compact requests
    /// with this model itself. References by ID survive proxy name renames.
    /// </summary>
    public int? ContextSummarizeModelId { get; set; }

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
    /// When true, known sensitive JSON fields such as authorization, API keys, tokens, secrets, and
    /// passwords are redacted. Prompt/message content is left intact so captured bodies remain useful
    /// for diagnostics. Applies when body-level redaction is disabled but detail capture is enabled.
    /// </summary>
    public bool RedactSensitiveJsonFields { get; set; } = true;

    /// <summary>
    /// Model context window size in tokens. When 0 (default), uses <see cref="DefaultContextWindowTokens"/>.
    /// Override per-model if the auto-default is incorrect (e.g., qwen-max is 32K, qwen-long is 10M).
    /// This value is advertised to clients via /api/show model_info and used by clients like GitHub Copilot
    /// to determine context compaction thresholds.
    /// </summary>
    public int ContextWindowTokens { get; set; }

    /// <summary>
    /// Default context window when <see cref="ContextWindowTokens"/> is not explicitly set (0).
    /// Conservative fallback that works for most models; override per-model if needed.
    /// </summary>
    public const int DefaultContextWindowTokens = 131072;

    /// <summary>
    /// Returns the effective context window for this mapping: the explicit value if set, otherwise the default.
    /// </summary>
    public int GetEffectiveContextWindow() => ContextWindowTokens > 0 ? ContextWindowTokens : DefaultContextWindowTokens;

    /// <summary>
    /// Proactive context-overflow threshold as a percentage of the effective context window (1–100).
    /// When the proxy estimates the incoming request exceeds this percentage of the context window,
    /// it returns 413 immediately without calling upstream. 0 disables (default).
    /// </summary>
    public int ProactiveOverflowPercent { get; set; }

    /// <summary>
    /// Proactive context-overflow threshold as an absolute token count.
    /// When set (> 0), takes precedence over <see cref="ProactiveOverflowPercent"/>.
    /// 0 disables (default).
    /// </summary>
    public int ProactiveOverflowTokens { get; set; }

    /// <summary>
    /// Resolves the effective proactive overflow threshold in tokens, or 0 if the feature is disabled.
    /// Absolute token count takes precedence over percentage.
    /// </summary>
    public int GetProactiveOverflowThreshold()
    {
        if (ProactiveOverflowTokens > 0)
            return ProactiveOverflowTokens;
        if (ProactiveOverflowPercent > 0)
            return (int)(GetEffectiveContextWindow() * ProactiveOverflowPercent / 100.0);
        return 0;
    }

    /// <summary>
    /// Creates a deep copy of this ModelMapping instance with all properties cloned,
    /// preserving the stable <see cref="Id"/> so cross-mapping references (e.g.
    /// <see cref="ContextSummarizeModelId"/>) remain valid across clones. Call
    /// <see cref="AssignNewId"/> on the result when a truly independent mapping is needed
    /// (e.g. duplicating a row).
    /// </summary>
    public ModelMapping Clone()
    {
        ModelMapping clone = new()
        {
            Id = Id,
            IsEnabled = IsEnabled,
            ProxyName = ProxyName,
            ModelName = ModelName,
            EnableThinkingCompatibility = EnableThinkingCompatibility,
            Capabilities = [.. Capabilities],
            EnableHeartbeats = EnableHeartbeats,
            CredentialName = CredentialName,
            UpstreamType = UpstreamType,
            ThinkingMode = ThinkingMode,
            UpstreamUrl = UpstreamUrl,
            UpstreamTimeoutSeconds = UpstreamTimeoutSeconds,
            RepeatPenalty = RepeatPenalty,
            TemperaturePriority = TemperaturePriority,
            RepeatPenaltyPriority = RepeatPenaltyPriority,
            Temperature = Temperature,
            ReasoningEffortPriority = ReasoningEffortPriority,
            ReasoningEffort = ReasoningEffort,
            ReasoningEffortValues = [.. ReasoningEffortValues],
            ReasoningEffortFormat = ReasoningEffortFormat,
            InstructionSetName = InstructionSetName,
            ContextSummarizeModelId = ContextSummarizeModelId,
            RedactRequestBodies = RedactRequestBodies,
            RedactResponseBodies = RedactResponseBodies,
            RedactSensitiveJsonFields = RedactSensitiveJsonFields,
            ContextWindowTokens = ContextWindowTokens,
            ProactiveOverflowPercent = ProactiveOverflowPercent,
            ProactiveOverflowTokens = ProactiveOverflowTokens,
        };
        clone.EnsureId();
        return clone;
    }

    /// <summary>
    /// Replaces this mapping's stable <see cref="Id"/> with a freshly generated unique one.
    /// Used when duplicating a mapping so the copy becomes an independent mapping.
    /// </summary>
    internal void AssignNewId()
    {
        Id = 0;
        EnsureId();
    }
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
    /// Request-log growth is controlled by retention cleanup instead of database-file archiving.
    /// This setting is no longer used by the SQLite-backed database.
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

    /// <summary>
    /// Maximum allowed request body size in bytes. Requests with a body larger than this are
    /// rejected with 413 Payload Too Large before being buffered, protecting the proxy from
    /// memory-exhaustion attacks. Default: 10 MB.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent in-flight proxy requests. When this many requests are being
    /// processed, additional incoming requests are rejected with 503 Service Unavailable instead of
    /// queueing without bound. Default: 64.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 64;

    /// <summary>
    /// When true, the proxy emits permissive CORS headers (Access-Control-Allow-Origin: *) and
    /// answers OPTIONS preflight requests, allowing browser-based clients to call it directly.
    /// Default: false. Keep false when the proxy is only reached backend-to-backend (e.g. behind a
    /// load balancer/WAF), since a wildcard CORS policy lets any webpage drive the proxy from a browser.
    /// </summary>
    public bool EnableCors { get; set; } = false;

    /// <summary>Model name mappings loaded from the application database at startup.</summary>
    [JsonIgnore]
    public List<ModelMapping> ModelMappings { get; set; } = [];

    /// <summary>Named instruction sets loaded from the application database at startup.</summary>
    [JsonIgnore]
    public List<InstructionSet> InstructionSets { get; set; } = [];

    /// <summary>Named credentials (secrets) loaded from the application database at startup.</summary>
    [JsonIgnore]
    public List<StoredCredential> Credentials { get; set; } = [];

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
    /// When true, the application re-launches itself elevated (UAC prompt) at startup so http.sys
    /// accepts non-localhost listener bindings (e.g. 0.0.0.0) without a manual "Run as
    /// administrator". Ignored in debug builds. Takes effect on the next launch. Default: false.
    /// </summary>
    [JsonIgnore]
    public bool RunAsAdministrator { get; set; } = false;

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
    /// When true, each request log entry captures the proxy's before/after transformation:
    /// the raw upstream response body and a human-readable summary of every settings-driven
    /// override applied for the model (temperature, repeat_penalty, instruction-set
    /// injection, reasoning effort, model rewrite). Purely additive: it does not alter
    /// <see cref="CollectRequestDetails"/> / <see cref="CollectResponseDetails"/> behavior.
    /// Default: false.
    /// </summary>
    [JsonIgnore]
    public bool DebugMode { get; set; } = false;

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

    /// <summary>
    /// When true, the dashboard periodically samples CPU and memory usage for display.
    /// Disable to reduce background overhead. Default: true.
    /// </summary>
    [JsonIgnore]
    public bool EnablePerformanceSampling { get; set; } = true;

    /// <summary>
    /// When true, the proxy serves a Scalar API explorer at /scalar and an OpenAPI
    /// specification at /openapi/v1/openapi.json, allowing browser-based exploration of all
    /// proxy endpoints. Documents reported by loaded modules appear in the same explorer
    /// dropdown. Default: false.
    /// </summary>
    [JsonIgnore]
    public bool EnableApiExplorer { get; set; } = false;

    /// <summary>Logging configuration.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// Optional passphrase persisted in settings.jsonc used to decrypt model-mapping API keys.
    /// When set, encrypted API keys are decrypted automatically at startup without prompting.
    /// Leave null to require the user to enter the passphrase each launch.
    /// </summary>
    public string? SecurityPassphrase { get; set; }

    /// <summary>
    /// Session-only passphrase used to encrypt/decrypt API keys. Populated at startup from
    /// <see cref="SecurityPassphrase"/> or from the launch-time prompt. Never persisted to disk.
    /// </summary>
    [JsonIgnore]
    public string? RuntimePassphrase { get; set; }

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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {Path}, using defaults", _settingsPath);
            return new AppSettings();
        }
    }

    public void Save()
    {
        // Clamp/validate before persisting so the on-disk configuration is always sane.
        Normalize();

        try
        {
            string dir = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, _writeOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _settingsPath);
            throw;
        }
    }

    /// <summary>
    /// Clamps mutable settings to their valid ranges and validates model-mapping URLs. Called at the
    /// start of <see cref="Save"/> so persisted configuration is always sane — protecting the proxy
    /// from invalid values (for example, a non-positive upstream timeout would create an
    /// already-cancelled <see cref="System.Threading.CancellationTokenSource"/>, and an out-of-range
    /// port would fail to bind). Numeric values are clamped silently; an unsupported upstream URL
    /// scheme is logged as a warning (the mapping will simply fail to connect at request time). This
    /// method never throws, so it is safe to invoke from every <c>Save()</c> call site.
    /// </summary>
    public void Normalize()
    {
        ListenPort = Math.Clamp(ListenPort, 1, 65535);
        MaxConcurrentRequests = Math.Clamp(MaxConcurrentRequests, 1, 10000);
        MaxRequestBodyBytes = Math.Max(MaxRequestBodyBytes, 1024);
        MaxLogEntries = Math.Clamp(MaxLogEntries, 10, 100000);
        StreamingHeartbeatIntervalSeconds = Math.Clamp(StreamingHeartbeatIntervalSeconds, 5, 300);

        foreach (ModelMapping mapping in ModelMappings)
        {
            if (mapping.UpstreamTimeoutSeconds <= 0)
                mapping.UpstreamTimeoutSeconds = 300;

            mapping.Temperature = Math.Clamp(mapping.Temperature, 0.0, 2.0);
            mapping.RepeatPenalty = Math.Clamp(mapping.RepeatPenalty, 0.0, 2.0);

            if (!string.IsNullOrWhiteSpace(mapping.UpstreamUrl)
                && Uri.TryCreate(mapping.UpstreamUrl, UriKind.Absolute, out Uri? uri)
                && !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning(
                    "Model mapping '{ProxyName}' has unsupported upstream URL scheme '{Scheme}' ({Url}); only http and https are supported",
                    mapping.ProxyName, uri.Scheme, mapping.UpstreamUrl);
            }
        }
    }

    public RuntimeSettings CreateRuntimeSettings() => new()
    {
        AutoStartProxy = AutoStartProxy,
        StartWithDashboardOpen = StartWithDashboardOpen,
        AllowMultipleInstances = AllowMultipleInstances,
        RunAsAdministrator = RunAsAdministrator,
        ShowCloseToTrayNotification = ShowCloseToTrayNotification,
        CollectRequestDetails = CollectRequestDetails,
        CollectResponseDetails = CollectResponseDetails,
        DebugMode = DebugMode,
        EnableStreamingHeartbeats = EnableStreamingHeartbeats,
        StreamingHeartbeatIntervalSeconds = StreamingHeartbeatIntervalSeconds,
        EnablePerformanceSampling = EnablePerformanceSampling,
        EnableApiExplorer = EnableApiExplorer,
    };

    public void ApplyRuntimeSettings(RuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        AutoStartProxy = runtimeSettings.AutoStartProxy;
        StartWithDashboardOpen = runtimeSettings.StartWithDashboardOpen;
        AllowMultipleInstances = runtimeSettings.AllowMultipleInstances;
        RunAsAdministrator = runtimeSettings.RunAsAdministrator;
        ShowCloseToTrayNotification = runtimeSettings.ShowCloseToTrayNotification;
        CollectRequestDetails = runtimeSettings.CollectRequestDetails;
        CollectResponseDetails = runtimeSettings.CollectResponseDetails;
        DebugMode = runtimeSettings.DebugMode;
        EnableStreamingHeartbeats = runtimeSettings.EnableStreamingHeartbeats;
        StreamingHeartbeatIntervalSeconds = runtimeSettings.StreamingHeartbeatIntervalSeconds;
        EnablePerformanceSampling = runtimeSettings.EnablePerformanceSampling;
        EnableApiExplorer = runtimeSettings.EnableApiExplorer;
    }

    /// <summary>
    /// Resolves a requested model name to the upstream model name.
    /// Returns the mapped upstream name if an enabled mapping is found, otherwise returns the original name unchanged.
    /// </summary>
    public string ResolveModelName(string requestedModel)
    {
        string normalizedRequested = NormalizeModelTag(requestedModel);

        foreach (ModelMapping mapping in ModelMappings)
        {
            if (mapping.IsEnabled && string.Equals(NormalizeModelTag(mapping.ProxyName), normalizedRequested, StringComparison.OrdinalIgnoreCase))
                return mapping.ModelName;
        }

        return requestedModel;
    }

    /// <summary>
    /// Finds an enabled model mapping by either the exposed proxy name or the upstream model name.
    /// Returns null when no enabled configured mapping matches. Comparisons ignore any Ollama-style
    /// ":tag" suffix (e.g. "myqwen:latest" matches a mapping configured as "myqwen"), since Ollama
    /// clients commonly append ":latest" even when the proxy name has no tag.
    /// </summary>
    public ModelMapping? FindModelMapping(string requestedModel)
    {
        string normalizedRequested = NormalizeModelTag(requestedModel);

        foreach (ModelMapping mapping in ModelMappings)
        {
            if (!mapping.IsEnabled)
                continue;

            if (string.Equals(NormalizeModelTag(mapping.ProxyName), normalizedRequested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeModelTag(mapping.ModelName), normalizedRequested, StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a model mapping by its stable <see cref="ModelMapping.Id"/>, regardless of whether
    /// it is enabled. Returns null when no mapping with the given ID exists. Used by cross-mapping
    /// references (e.g. <see cref="ModelMapping.ContextSummarizeModelId"/>) so that renames of the
    /// target mapping's <see cref="ModelMapping.ProxyName"/> do not break the reference.
    /// </summary>
    public ModelMapping? FindModelMappingById(int id)
    {
        if (id == 0)
            return null;

        foreach (ModelMapping mapping in ModelMappings)
        {
            if (mapping.Id == id)
                return mapping;
        }

        return null;
    }

    /// <summary>
    /// Strips a trailing Ollama-style ":tag" suffix (e.g. ":latest") from a model name for
    /// comparison purposes. Returns the original string unchanged when no tag suffix is present.
    /// </summary>
    public static string NormalizeModelTag(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return string.Empty;

        int colonIndex = modelName.IndexOf(':');
        return colonIndex < 0 ? modelName : modelName[..colonIndex];
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

    /// <summary>
    /// Finds a stored credential by name (case-insensitive).
    /// Returns null when no credential with the given name exists.
    /// </summary>
    public StoredCredential? FindCredential(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (StoredCredential credential in Credentials)
        {
            if (string.Equals(credential.Name, name, StringComparison.OrdinalIgnoreCase))
                return credential;
        }

        return null;
    }

    /// <summary>
    /// Resolves the effective bearer API key for a model mapping by looking up the referenced
    /// stored credential (<see cref="ModelMapping.CredentialName"/>). Returns null when no
    /// credential is referenced or the credential does not exist.
    /// </summary>
    public string? ResolveApiKey(ModelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        StoredCredential? credential = FindCredential(mapping.CredentialName);
        return credential is not null && !string.IsNullOrWhiteSpace(credential.Secret)
            ? credential.Secret
            : null;
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
