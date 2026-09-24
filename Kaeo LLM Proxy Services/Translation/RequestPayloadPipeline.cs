using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Everything the request-payload steps need in order to decide. The context is populated in
/// order as the pipeline runs, because later steps depend on decisions earlier ones recorded -
/// the same dependency chain the legacy single-pass writer encoded implicitly.
/// </summary>
internal sealed class RequestPayloadContext
{
    /// <summary>The client's body, exactly as received. Untouched, so the fast path can return it.</summary>
    public required string OriginalJson { get; init; }

    /// <summary>The client's body parsed for rewriting. Mutated by the steps.</summary>
    public required JsonObject Payload { get; init; }

    public required AppSettings Settings { get; init; }

    public required RequestLog Log { get; init; }

    /// <summary>The model the client asked for, before any redirect.</summary>
    public string OriginalModel { get; set; } = string.Empty;

    /// <summary>The compaction target when a /compact redirect applied, else <see cref="OriginalModel"/>.</summary>
    public string EffectiveModel { get; set; } = string.Empty;

    /// <summary>The upstream model name the request is rewritten to send.</summary>
    public string ResolvedModel { get; set; } = string.Empty;

    /// <summary>True when a context-summarize request was redirected to its compaction target.</summary>
    public bool CompactRedirected { get; set; }

    /// <summary>The mapping governing the effective model, or null when unmapped.</summary>
    public ModelMapping? Mapping { get; set; }

    /// <summary>Instruction text to inject, or null when the mapping has no instruction set.</summary>
    public string? InjectedInstructions { get; set; }

    /// <summary>Whether the thinking-compatibility rules apply to the effective model.</summary>
    public bool ApplyThinkingCompatibility { get; set; } = true;

    /// <summary>What the client asked for in stream_options, and whether the proxy took it over.</summary>
    public StreamOptionsInfo StreamOptions { get; set; } = StreamOptionsInfo.None;

    /// <summary>Set once the leading-system and prefill scan has run.</summary>
    public bool HasConsecutiveSystemMessages { get; set; }

    /// <summary>True when the conversation ends with an assistant prefill the upstream would reject.</summary>
    public bool HasTrailingAssistantPrefill { get; set; }

    /// <summary>True when instruction text has to be folded into the system message.</summary>
    public bool ShouldInjectInstructions { get; set; }

    /// <summary>True when any step changed the payload, so the rewrite must be serialized.</summary>
    public bool Rewritten { get; set; }

    /// <summary>
    /// Debug-summary lines the steps want recorded. Collected rather than written directly so a step
    /// does not have to know whether debug mode is on or how the summary is formatted.
    /// </summary>
    public List<string> PendingNotes { get; } = [];

    // The client's own sampling values, captured before any step mutates the payload so the debug
    // summary can report a before/after comparison.
    public float? ClientTemperature { get; set; }
    public float? ClientRepeatPenalty { get; set; }
    public string? ClientReasoningEffort { get; set; }

    // Resolved sampling decisions. Recorded by the steps that make them so the debug summary does
    // not have to re-derive the same logic and risk disagreeing with what was actually applied.
    public SamplingPriority TemperaturePriority { get; set; } = SamplingPriority.ClientApp;
    public SamplingPriority RepeatPenaltyPriority { get; set; } = SamplingPriority.ClientApp;
    public double ProxyTemperature { get; set; } = 0.7;
    public double ProxyRepeatPenalty { get; set; } = 1.0;
    public SamplingPriority ReasoningEffortPriority { get; set; } = SamplingPriority.ClientApp;
    public string ProxyReasoningEffort { get; set; } = string.Empty;
    public ReasoningEffortFormat ReasoningEffortFormat { get; set; } = ReasoningEffortFormat.Legacy;
}

/// <summary>
/// One named, independently testable payload transformation.
/// </summary>
/// <remarks>
/// Steps are deliberately free of ordering concerns: the pipeline owns the order, each step owns
/// one concern. That is what makes the set of payload changes a single consistent transformer
/// instead of one bespoke branch per provider.
/// </remarks>
internal interface IRequestPayloadStep
{
    /// <summary>Stable name used in debug notes and failure reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this step has anything to do for the given context. A step that does not apply must
    /// leave the payload byte-identical, so the pipeline can keep the original text.
    /// </summary>
    bool Applies(RequestPayloadContext context);

    /// <summary>Applies the transformation in place.</summary>
    void Apply(RequestPayloadContext context);
}