namespace Kaeo.LlmProxy.Modules;

/// <summary>
/// A host-provided sink that lets modules record their activity into the host's MCP request
/// log (the Logs tab, MCP sub-tab). Entries appear alongside the MCP server's HTTP request
/// rows and are persisted with the same retention rules. The host binds the sink to the MCP
/// log store after module initialization, so implementations must tolerate entries at any time.
/// </summary>
public interface IMcpActivityLog
{
    /// <summary>Records one activity entry. Implementations must not throw.</summary>
    void Write(McpActivityEntry entry);
}

/// <summary>
/// One activity entry written by a module through <see cref="IMcpActivityLog"/>. Maps onto the
/// host's request log: <see cref="Source"/> becomes the Method column, Operation and Target form
/// the Path column, and the optional details become the request/response bodies shown in the
/// log detail view.
/// </summary>
/// <param name="Source">Short source label shown in the Method column, e.g. "SSH".</param>
/// <param name="Operation">Operation verb, e.g. "connect", "exec", "disconnect", "close".</param>
public sealed record McpActivityEntry(string Source, string Operation)
{
    /// <summary>Target the operation applies to, e.g. "noone@192.168.101.1:22".</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>True when the operation failed.</summary>
    public bool IsError { get; init; }

    /// <summary>True when the operation was cancelled or timed out.</summary>
    public bool IsCancelled { get; init; }

    /// <summary>Result/status code associated with the operation, e.g. a remote exit code.</summary>
    public int StatusCode { get; init; }

    /// <summary>Wall-clock duration of the operation in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Failure reason when <see cref="IsError"/> or <see cref="IsCancelled"/> is set.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Optional request detail, e.g. the executed command or connect parameters.</summary>
    public string? RequestDetail { get; init; }

    /// <summary>Optional response detail, e.g. the full command output.</summary>
    public string? ResponseDetail { get; init; }
}
