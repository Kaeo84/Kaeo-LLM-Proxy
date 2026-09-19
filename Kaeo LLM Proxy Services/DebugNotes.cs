using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Builds the human-readable audit trail of every settings-driven override and transformation
/// the proxy applied for a single request, surfaced at the top of the log details when
/// <see cref="AppSettings.DebugMode"/> is on. Each line states the effective value and whether
/// it was injected (new value added), replaced (client value overridden), passed through
/// (client value kept), or omitted (dropped entirely). The static helpers are pure so the
/// wording is unit-testable without a running handler.
/// </summary>
internal static class DebugNotes
{
    /// <summary>
    /// Describes the model-name resolution: a mapping rewrite or a pass-through when no
    /// enabled mapping matches the requested name.
    /// </summary>
    /// <remarks>
    /// The sides are labelled because this line is commonly printed directly under a
    /// <see cref="ContextSummarizeRedirectPassthrough"/> line, which also uses an arrow but for a
    /// different transformation. Unlabelled, the target of one line becomes the source of the
    /// next and the pair reads as reversed.
    /// </remarks>
    public static string ModelResolution(string proxyName, string resolved, bool mapped)
    {
        if (!mapped || string.Equals(proxyName, resolved, StringComparison.OrdinalIgnoreCase))
            return $"model: \"{proxyName}\" (no mapping — passed through unchanged)";

        return $"model: proxy name \"{proxyName}\" → upstream model \"{resolved}\" (mapping \"{proxyName}\")";
    }

    /// <summary>
    /// Describes a context-summarize (/compact) redirect: the request was detected as a Copilot
    /// session-summary request and routed to the smaller/faster compact model configured on the
    /// mapping, instead of the model the client originally requested.
    /// </summary>
    public static string ContextSummarizeRedirect(string originalModel, string compactModel) =>
        $"model: /compact request for client model \"{originalModel}\" → routed to compaction model \"{compactModel}\" (context-summarize compact model)";

    /// <summary>
    /// Describes a sampling field (e.g. <c>temperature</c> or <c>repeat_penalty</c>) decision
    /// for the given per-model priority, the client's value, and the proxy's configured value.
    /// </summary>
    public static string SamplingDecision(
        string field,
        SamplingPriority priority,
        float? clientValue,
        float proxyValue)
    {
        string clientDesc = clientValue.HasValue ? clientValue.Value.ToString("0.####") : "none";

        return priority switch
        {
            SamplingPriority.Provider =>
                $"{field}: omitted (provider priority — client value {clientDesc} not forwarded)",
            SamplingPriority.Proxy when clientValue.HasValue =>
                $"{field}: {proxyValue:0.####} (proxy override — replaced client value {clientValue:0.####})",
            SamplingPriority.Proxy =>
                $"{field}: {proxyValue:0.####} (injected — client sent none)",
            _ when clientValue.HasValue =>
                $"{field}: {clientValue:0.####} (client value passed through)",
            _ =>
                $"{field}: not set (client sent none)",
        };
    }

    /// <summary>
    /// Describes instruction-set injection: the named set's text was prepended as a system message.
    /// </summary>
    public static string InstructionInjection(string setName) =>
        $"system prompt: injected instruction set \"{setName}\" (prepended as system message)";

    /// <summary>
    /// Describes the reasoning-effort decision: the effective value and which priority produced it,
    /// plus every wire format the value is emitted through under proxy injection.
    /// </summary>
    public static string ReasoningEffortDecision(
        SamplingPriority priority,
        string? clientEffort,
        string? proxyEffort,
        ReasoningEffortFormat format)
    {
        string clientDesc = string.IsNullOrWhiteSpace(clientEffort) ? "none" : $"'{clientEffort}'";

        if (priority == SamplingPriority.Provider)
            return $"reasoning_effort: omitted (provider priority — client {clientDesc} dropped)";

        if (priority == SamplingPriority.Proxy && !string.IsNullOrWhiteSpace(proxyEffort))
        {
            string action = string.IsNullOrWhiteSpace(clientEffort)
                ? "injected"
                : $"replaced client {clientDesc}";
            return $"reasoning_effort: '{proxyEffort}' (proxy priority — {action} via {DescribeFormats(format)})";
        }

        if (!string.IsNullOrWhiteSpace(clientEffort))
            return $"reasoning_effort: '{clientEffort}' (client value passed through)";

        return "reasoning_effort: not set (client sent none)";
    }

    /// <summary>
    /// Describes the upstream routing decision: which mapping was resolved, the upstream URL
    /// the request will be sent to, whether a credential is attached, and the timeout. This
    /// makes it visible in the debug summary whether a compact redirect actually changed the
    /// upstream target or whether the request is still hitting the main model's server.
    /// </summary>
    public static string UpstreamRouting(
        string mappingName,
        string upstreamUrl,
        bool hasCredential,
        int timeoutSeconds)
    {
        string credDesc = hasCredential ? "credential attached" : "no credential (client token passes through)";
        return $"upstream: mapping \"{mappingName}\" → {upstreamUrl} ({credDesc}, {timeoutSeconds}s timeout)";
    }

    /// <summary>
    /// Describes a context-summarize (/compact) redirect for the passthrough debug summary:
    /// the model the client actually asked for, the configured compaction model it was routed
    /// to, and the reason (always "context-summarize signature detected").
    /// </summary>
    /// <remarks>
    /// Both sides are named rather than left as a bare arrow, because the
    /// <see cref="ModelResolution"/> line printed immediately below also uses an arrow — for the
    /// proxy-name-to-upstream-name rewrite — and it starts from this line's target. Unlabelled,
    /// the two read as a single reversed chain.
    /// </remarks>
    public static string ContextSummarizeRedirectPassthrough(string originalModel, string compactModel) =>
        $"compact redirect: client requested \"{originalModel}\" → routed to compaction model \"{compactModel}\" (context-summarize signature detected)";

    /// <summary>
    /// Describes where a manual compaction request was forwarded. When <paramref name="redirected"/>
    /// is true the mapping opted in via "Redirect manual compaction" and the request went to its
    /// configured compaction model; otherwise it went to the model the client asked for so that
    /// model produces its own summary.
    /// </summary>
    public static string ManualCompactionTarget(string originalModel, string upstreamModel, bool redirected) =>
        redirected
            ? $"compaction: client requested \"{originalModel}\" → forwarded to compaction model's upstream \"{upstreamModel}\" (redirected to the configured compaction model)"
            : $"compaction: client requested \"{originalModel}\" → forwarded to its own upstream \"{upstreamModel}\" (no redirect — the requested model summarizes itself)";

    private static string DescribeFormats(ReasoningEffortFormat format)
    {
        List<string> parts = [];
        if (format.HasFlag(ReasoningEffortFormat.Legacy))
            parts.Add("legacy reasoning_effort");
        if (format.HasFlag(ReasoningEffortFormat.Modern))
            parts.Add("modern reasoning.enable+thinking_level");
        if (format.HasFlag(ReasoningEffortFormat.QwenCloud))
            parts.Add("Qwen Cloud extra_body");
        if (format.HasFlag(ReasoningEffortFormat.ChatTemplateKwargs))
            parts.Add("chat_template_kwargs");
        return parts.Count == 0 ? "legacy reasoning_effort" : string.Join(" + ", parts);
    }
}
