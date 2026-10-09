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

    /// <summary>
    /// Name of the seeded instruction set that drives compaction summarization. Preselected in the
    /// Settings tab so the operator has an editable prompt rather than an opaque built-in one.
    /// </summary>
    internal const string CompactionInstructionSetName = "Compaction";

    /// <summary>
    /// The canonical compaction summarizer prompt, and the proxy's built-in default when no
    /// instruction set is selected.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the compaction service because the seed data and the runtime
    /// default must be the same text: a fresh install and an install with no selection would
    /// otherwise summarize differently for no visible reason.
    /// <para>
    /// Deliberately avoids GitHub's literal signature tokens — <c>authoritative, self-contained
    /// summary</c>, <c>&lt;ConversationSummary&gt;</c>, <c>ReasoningScratchpad</c>. Those are how
    /// <c>OllamaProxyHandler.IsContextSummarizeRequest</c> recognizes a <c>/compact</c> request, and
    /// the compacted body places this summary in the conversation's first message. Emitting one would
    /// make the next turn look like a fresh compaction request. The checkpoint concept is carried in
    /// prose instead.
    /// </para>
    /// <para>
    /// The <c>## Tool activity</c> requirement is load-bearing: compacted conversations must still
    /// record which tools ran and what they returned, and dropping it silently loses that.
    /// </para>
    /// </remarks>
    internal const string CompactionSummarizerInstructions =
        "You are summarizing a conversation so that work can continue from a smaller context. " +
        "Produce a concise, self-contained project checkpoint of the conversation chunk that follows. " +
        "Preserve: decisions made and their rationale; files, paths, and identifiers touched; commands run and their outcomes; " +
        "errors encountered and how they were resolved; and any stated requirements, constraints, or preferences. " +
        "Drop pleasantries, restatements, and superseded detail. " +
        "If the transcript includes a <toolcalls> section, those are tool invocations and their results. " +
        "You MUST end your summary with a '## Tool activity' section listing each tool called, whether it succeeded or failed, " +
        "and any important outputs (file paths, command results, errors, and decisions made from them). Never omit tool activity.";

    /// <summary>
    /// Summarizer prompt used when the conversation being compacted is GitHub Copilot's own
    /// session-summary request.
    /// </summary>
    /// <remarks>
    /// Copilot's system prompt already demands a specific XML skeleton and is preserved verbatim into
    /// the compacted body, sitting immediately before the summary text. That instruction therefore
    /// outranks anything a generic prompt says, and a prompt asking for plain prose competes with it
    /// rather than helping. The observed result was a response whose later sections were still the
    /// template's own placeholder text, because nothing had told the model the skeleton mattered.
    /// <para>
    /// So this prompt does not restate a format of its own. It defers to the skeleton Copilot already
    /// supplied and reinforces two things the template cannot say for itself: fill every section rather
    /// than leaving a placeholder, and prefer completeness over brevity — the template is large and a
    /// summary that stops early is unusable to the next session.
    /// </para>
    /// <para>
    /// The <c>## Tool activity</c> requirement is intentionally absent here. Copilot's skeleton has no
    /// section for it, and asking for a heading the template does not contain invites the model to
    /// emit text outside the XML — which the client explicitly forbids.
    /// </para>
    /// </remarks>
    internal const string CopilotCompactionSummarizerInstructions =
        "You are summarizing a GitHub Copilot session so a fresh session can continue the work from a smaller context. " +
        "The conversation's system message specifies the exact summary format required. Follow that format exactly: obey the section names, " +
        "nesting, and order it gives. " +
        "Fill in EVERY section of that format with real content drawn from the conversation that follows. " +
        "Do not leave placeholder text, bracketed guidance, or an element empty — a section with nothing to report should say so briefly " +
        "rather than repeating instructions, and a section that does not apply should be omitted instead of filled with a note. " +
        "Prefer completeness over brevity: the required format is detailed, and a summary that stops part-way through it is unusable. " +
        "Preserve decisions and their rationale, files and identifiers touched, commands run and their outcomes, errors and how they were " +
        "resolved, and any stated requirements or constraints. Drop pleasantries and superseded detail. " +
        "Output only the required format, with no commentary before or after it.";

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

    /// <summary>
    /// Creates the seeded compaction instruction set, carrying the same prompt the proxy uses as its
    /// built-in summarizer default.
    /// </summary>
    /// <remarks>
    /// Seeded as an editable instruction set rather than left implicit so the operator can see and
    /// change what the compaction model is told. The text must match the built-in default, or a fresh
    /// install and an install with no selection would summarize differently for no visible reason.
    /// <para>
    /// The <c>## Tool activity</c> requirement is load-bearing: compacted conversations must still
    /// record which tools ran and what they returned, and dropping it silently loses that.
    /// </para>
    /// </remarks>
    internal static InstructionSet CreateCompactionInstructionSet() => new()
    {
        Name = CompactionInstructionSetName,
        Instructions = CompactionSummarizerInstructions,
        Description =
            "Summarizer prompt used for every context compaction. Seeded from the proxy's built-in "
            + "default; edit to change how compacted conversations are summarized.",
    };
}
