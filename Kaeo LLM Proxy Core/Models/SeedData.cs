namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// The developer model mappings and credentials written into a brand-new application database so a
/// clean build opens onto a working configuration instead of an empty Models list. Populated by
/// <c>AppDatabase</c> only when <c>model_mappings</c> holds no rows, so a real configuration is
/// never overwritten or duplicated.
/// </summary>
internal static class SeedData
{
    /// <summary>Name of the credential shared by the hosted Qwen Cloud mappings.</summary>
    internal const string QwenCloudCredentialName = "QwenCloud Token Plan";

    /// <summary>Qwen Cloud token-plan OpenAI-compatible base URL shared by the hosted mappings.</summary>
    private const string QwenCloudUrl = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1";

    /// <summary>
    /// Placeholder secret for the seeded credential. It is not a usable key: the mapping works only
    /// once the real token is entered on the Credentials tab, at which point it is encrypted at rest.
    /// </summary>
    private const string PlaceholderSecret = "seed-placeholder-replace-with-real-token";

    private const string QwenCloudDescription =
        "Seeded placeholder for the Qwen Cloud token-plan endpoint. Replace with a real token.";

    /// <summary>
    /// Creates the default set of model mappings with fresh identifiers assigned from the shared
    /// mapping id counter, so they cannot collide with mappings created later in the same process.
    /// </summary>
    internal static List<ModelMapping> CreateModelMappings()
    {
        List<ModelMapping> mappings =
        [
            CreateQwenCloudMapping("QC Qwen 3.8 Max", "qwen3.8-max", 1_000_000),
            CreateQwenCloudMapping("QC Qwen 3.8 Flash", "qwen3.8-flash", 1_000_000),
            CreateQwenCloudMapping("QC Qwen 3.7 Plus", "qwen3.7-plus", 1_000_000),
            CreateMapping(
                "Desktop - Qwen",
                @"..\Model\Qwen3.8-Unsloth-NVFP4\Qwen3.8-27B-NVFP4-MTP-VERY-HIGH.gguf",
                "http://192.168.101.199:8080",
                credentialName: null,
                contextWindowTokens: 256_000),
            CreateMapping(
                "Svr Qwen3 Embedding 4B",
                @"..\Model\hugger7326-Qwen3-Embedding-4B-Q8_0-GGUF\Qwen3-Embedding-4B-Q8_0.gguf",
                "http://127.0.0.1:8081",
                credentialName: null,
                contextWindowTokens: 2_048),
        ];

        foreach (ModelMapping mapping in mappings)
            mapping.EnsureId();

        return mappings;
    }

    /// <summary>Creates the credentials referenced by <see cref="CreateModelMappings"/>.</summary>
    internal static List<StoredCredential> CreateCredentials() =>
    [
        new()
        {
            Name = QwenCloudCredentialName,
            Secret = PlaceholderSecret,
            Description = QwenCloudDescription,
        },
    ];

    private static ModelMapping CreateQwenCloudMapping(string proxyName, string modelName, int contextWindowTokens) =>
        CreateMapping(proxyName, modelName, QwenCloudUrl, QwenCloudCredentialName, contextWindowTokens);

    /// <summary>
    /// Builds one seeded mapping. Everything the seed catalog holds in common — the Qwen
    /// thinking/reasoning shape, the advertised capabilities, and redaction off so captured bodies
    /// stay readable during development — is applied here so the per-model entries above carry only
    /// what actually differs.
    /// </summary>
    private static ModelMapping CreateMapping(
        string proxyName,
        string modelName,
        string upstreamUrl,
        string? credentialName,
        int contextWindowTokens) => new()
    {
        ProxyName = proxyName,
        ModelName = modelName,
        UpstreamUrl = upstreamUrl,
        UpstreamType = UpstreamType.OpenAI,
        CredentialName = credentialName,
        ContextWindowTokens = contextWindowTokens,
        IsEnabled = true,

        // Client-facing SSE keep-alive, so streaming clients survive long thinking phases.
        EnableSseKeepAlive = true,

        // Qwen emits literal [Thinking]/[Answer] markers and rejects a prefilled assistant turn.
        EnableThinkingCompatibility = true,
        ThinkingMode = ThinkingMode.QwenThinkingCompatible,

        // Proxy priority is what makes the effort value and wire formats apply at all; the other
        // priorities pass the client's field through or omit it entirely.
        ReasoningEffortPriority = SamplingPriority.Proxy,
        ReasoningEffort = "medium",
        ReasoningEffortValues = ["medium"],
        ReasoningEffortFormat = ReasoningEffortFormat.Modern
            | ReasoningEffortFormat.QwenCloud
            | ReasoningEffortFormat.ChatTemplateKwargs,

        Capabilities = ["chat", "reasoning", "vision", "function_calling", "text"],

        // Redaction off: this is a local development database and readable captures are the point.
        RedactRequestBodies = false,
        RedactResponseBodies = false,
        RedactSensitiveJsonFields = false,
    };
}
