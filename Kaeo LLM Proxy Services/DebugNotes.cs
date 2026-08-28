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
    public static string ModelResolution(string proxyName, string resolved, bool mapped)
    {
        if (!mapped || string.Equals(proxyName, resolved, StringComparison.OrdinalIgnoreCase))
            return $"model: \"{proxyName}\" (no mapping — passed through unchanged)";

        return $"model: \"{proxyName}\" → \"{resolved}\" (mapping \"{proxyName}\")";
    }

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
