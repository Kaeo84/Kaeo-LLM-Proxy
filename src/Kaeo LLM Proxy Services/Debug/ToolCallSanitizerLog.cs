namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Collects the tool calls the declared-tool sanitizer removed from one response, so the debug
/// summary can state what was dropped instead of leaving it visible only in the raw capture.
/// </summary>
/// <remarks>
/// The sanitizer runs on three codings (non-streaming body transform, the legacy SSE rewriter, and
/// the IR frame translator) and drops calls one at a time as the response is forwarded. Each of
/// them records into the same instance, so a stream that drops the same undeclared name in twenty
/// frames produces one line, and a request that is not in debug mode allocates nothing.
/// </remarks>
internal sealed class ToolCallSanitizerLog
{
    // Insertion-ordered: a List for the summary wording plus a Set for dedupe, because HashSet
    // enumeration order is not contractual and the summary is compared in tests.
    private readonly List<string> _dropped = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a recorder for a response filtered against <paramref name="declaredToolNames"/>,
    /// which is only read to describe the reason a call was dropped.
    /// </summary>
    public ToolCallSanitizerLog(IReadOnlySet<string>? declaredToolNames)
    {
        ClientDeclaredNone = declaredToolNames is { Count: 0 };
        IsUnrestricted = declaredToolNames is null;
    }

    /// <summary>True when the client declared a <c>tools</c> array containing no functions.</summary>
    public bool ClientDeclaredNone { get; }

    /// <summary>True when no declared-tool set was supplied, so nothing can be dropped.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>Distinct function names dropped so far, in first-seen order.</summary>
    public IReadOnlyList<string> Dropped => _dropped;

    /// <summary>Records one dropped call. Unnamed calls are grouped under a placeholder.</summary>
    public void RecordDropped(string? functionName)
    {
        string name = string.IsNullOrWhiteSpace(functionName) ? "(unnamed)" : functionName;
        if (_seen.Add(name))
            _dropped.Add(name);
    }

    /// <summary>
    /// The debug-summary line for everything recorded, or null when nothing was dropped.
    /// </summary>
    public string? Describe()
        => _dropped.Count == 0 ? null : DebugNotes.ToolCallSanitizer(_dropped, ClientDeclaredNone);
}