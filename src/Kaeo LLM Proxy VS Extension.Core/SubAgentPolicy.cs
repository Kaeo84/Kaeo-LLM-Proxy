namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Pure validation and tool-scoping rules for sub-agent delegation (Defaults.SubAgents,
/// off by default). Kept host-agnostic so the policy is unit-testable; the extension's
/// vs_delegate tool supplies the runtime pieces (client, permission handler, agent list).
/// </summary>
public static class SubAgentPolicy
{
    /// <summary>The delegation tool itself; never available to a sub-agent.</summary>
    public const string DelegateToolName = "vs_delegate";

    /// <summary>Maximum nesting depth for v1: a sub-agent cannot delegate further.</summary>
    public const int MaxDepth = 1;

    /// <summary>Default tool-iteration budget for one sub-agent run (Defaults.SubAgentBudget overrides).</summary>
    public const int DefaultBudget = 25;

    /// <summary>
    /// Returns null when delegation is allowed, otherwise an honest refusal reason.
    /// <paramref name="depth"/> is the caller's depth (0 = the main turn).
    /// </summary>
    public static string? Validate(bool enabled, int depth)
    {
        if (!enabled)
            return "Sub-agents are disabled. Set Defaults.SubAgents=true in settings to enable vs_delegate.";
        if (depth >= MaxDepth)
            return $"Sub-agents cannot delegate further (depth cap {MaxDepth}). Complete the task directly.";
        return null;
    }

    /// <summary>
    /// Resolves the tool names a sub-agent may use. The requested scope (explicit tools argument,
    /// else the chosen agent's configured tools) is intersected with the parent turn's scope so a
    /// sub-agent can never exceed what the parent itself may call; null parent scope means "all".
    /// vs_delegate is always removed (depth cap enforced twice: here and in <see cref="Validate"/>).
    /// Unknown names pass through — tool projection drops names that are not actually available.
    /// </summary>
    public static List<string> ResolveTools(
        IReadOnlyList<string>? requested,
        IReadOnlyList<string>? parentTools,
        IReadOnlyList<string> allTools)
    {
        if (allTools is null) throw new ArgumentNullException(nameof(allTools));

        IEnumerable<string> scope = requested ?? parentTools ?? allTools;
        if (requested is not null && parentTools is not null)
            scope = scope.Where(t => parentTools.Contains(t, StringComparer.Ordinal));

        return scope
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, DelegateToolName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Effective iteration budget: the configured value when positive, else the default.</summary>
    public static int ResolveBudget(int configured) => configured > 0 ? configured : DefaultBudget;
}