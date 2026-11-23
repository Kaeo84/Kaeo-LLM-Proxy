using System;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Detects doom loops: the same tool call (name + identical arguments) failing the same
/// way N times in a row. Pure logic, no I/O — the host feeds it each executed call and
/// decides what to do when <see cref="Record"/> returns true.
/// </summary>
public sealed class DoomLoopDetector
{
    private readonly int _threshold;
    private string? _lastSignature;
    private int _consecutiveFailures;

    public DoomLoopDetector(int threshold = 3)
    {
        _threshold = Math.Max(2, threshold);
    }

    /// <summary>
    /// Records one executed tool call. Returns true when the identical call has now failed
    /// <c>threshold</c> times consecutively. A success or a different call resets the count.
    /// </summary>
    public bool Record(string toolName, string? argumentsJson, bool failed)
    {
        var signature = (toolName ?? string.Empty) + "\u0001" + (argumentsJson ?? string.Empty);
        if (failed && string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            _consecutiveFailures++;
        else
            _consecutiveFailures = failed ? 1 : 0;
        _lastSignature = signature;
        return _consecutiveFailures >= _threshold;
    }

    /// <summary>
    /// Heuristic failure detection over the runtime's string tool results. Matches the
    /// error conventions of the built-in tools and MCP host; a permission denial is the
    /// user's deliberate choice and never counts as a failure.
    /// </summary>
    public static bool LooksLikeFailure(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        var text = result!.TrimStart();
        if (text.StartsWith("Permission denied", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.StartsWith("Tool execution failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Unknown built-in tool", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Tool '", StringComparison.Ordinal) &&
            text.IndexOf("not found in any enabled MCP server", StringComparison.Ordinal) >= 0) return true;
        if (text.IndexOf("failed (exit ", StringComparison.Ordinal) >= 0) return true;
        if (text.StartsWith("Checkpoint failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Rollback failed", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}