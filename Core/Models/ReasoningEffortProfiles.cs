namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Known per-model reasoning effort profiles used to prefill the per-model reasoning effort
/// configuration for well-known model families, along with the standard value vocabulary that
/// OpenAI-compatible providers understand.
/// </summary>
internal static class ReasoningEffortProfiles
{
    /// <summary>
    /// Standard reasoning effort values understood by OpenAI-compatible providers, offered as
    /// suggestions in the configuration dialog.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardValues =
        ["low", "medium", "high", "xhigh", "max", "minimal", "none"];

    /// <summary>
    /// Attempts to match a known reasoning effort profile by model name (case-insensitive,
    /// tolerant of provider prefixes such as <c>kimi/kimi-k3</c>). Returns the supported values
    /// in priority order and the provider default value.
    /// </summary>
    public static bool TryGetProfile(string? modelName, out IReadOnlyList<string> values, out string defaultValue)
    {
        values = [];
        defaultValue = string.Empty;

        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        string model = modelName.Trim();

        // Kimi K3 only accepts maximum-intensity inference.
        if (model.Contains("kimi-k3", StringComparison.OrdinalIgnoreCase))
        {
            values = ["max"];
            defaultValue = "max";
            return true;
        }

        // Qwen3.8-Max: xhigh (default), medium, low. OpenAI standard values are mapped by the
        // provider; reasoning_effort and thinking_budget are mutually exclusive upstream.
        if (model.Contains("qwen3.8-max", StringComparison.OrdinalIgnoreCase))
        {
            values = ["xhigh", "medium", "low"];
            defaultValue = "xhigh";
            return true;
        }

        // DeepSeek-V4 and GLM series: high (default) and max; low/medium map to high and
        // xhigh maps to max on the provider side.
        if (model.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("glm-5", StringComparison.OrdinalIgnoreCase))
        {
            values = ["high", "max"];
            defaultValue = "high";
            return true;
        }

        return false;
    }
}
