namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Picks the model used for context compaction (Defaults.CompactionModel). Empty/unset keeps
/// the current chat model. A configured model that is not among the available models falls
/// back to the current one with an honest notice — compaction never silently uses a different
/// model than it reports. Model names match exactly (server-side names are case-sensitive).
/// </summary>
public static class CompactionModelSelector
{
    public sealed class Selection
    {
        public string ModelName { get; internal set; } = string.Empty;

        /// <summary>Status line to surface, or null when the current model is used as configured.</summary>
        public string? Notice { get; internal set; }

        /// <summary>True when a configured model was requested but unavailable.</summary>
        public bool UsedFallback { get; internal set; }

        internal static Selection Current(string model) => new() { ModelName = model };
    }

    public static Selection Select(string? configured, string currentModel, IReadOnlyList<string> availableModels)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Selection.Current(currentModel);

        var wanted = configured!.Trim();
        if (string.Equals(wanted, currentModel, StringComparison.Ordinal))
            return Selection.Current(currentModel);

        if (availableModels.Contains(wanted, StringComparer.Ordinal))
            return new Selection { ModelName = wanted, Notice = $"[compaction model: {wanted}]" };

        return new Selection
        {
            ModelName = currentModel,
            UsedFallback = true,
            Notice = $"[compaction model '{wanted}' is not an available model; using current model '{currentModel}']",
        };
    }
}
