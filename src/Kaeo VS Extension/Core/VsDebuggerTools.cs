using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

// Every DTE access is preceded by a main-thread switch (directly or via GetOpenDteAsync);
// the analyzer cannot prove thread affinity through the indirect call chain.
#pragma warning disable VSTHRD010

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Debugger control tools via the EnvDTE automation model: launch/stop/break/continue/step,
/// breakpoint management, stack/locals inspection, and expression evaluation. Every tool checks
/// the current debugger mode first and reports honestly when the requested action does not apply
/// (e.g. stepping while not paused). Waits for break/exit poll off the UI thread and re-switch
/// for each DTE read, mirroring the build tool's polling pattern.
/// </summary>
internal static class VsDebuggerTools
{
    private const int StartWaitMs = 60_000;
    private const int ContinueWaitMs = 30_000;
    private const int BreakWaitMs = 15_000;
    private const int StepWaitMs = 10_000;

    public static async Task<string> StartAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var mode = dte.Debugger.CurrentMode;
        if (mode != dbgDebugMode.dbgDesignMode)
            return $"Already debugging: debugger is in {ModeName(mode)} mode. Use vs_debug_continue or vs_debug_stop.";

        try
        {
            dte.ExecuteCommand("Debug.Start");
        }
        catch (Exception ex)
        {
            return $"Could not start debugging: {ex.Message}. Check that a startup project is set.";
        }

        var final = await WaitForBreakOrExitAsync(dte, StartWaitMs, ct);
        return final switch
        {
            dbgDebugMode.dbgBreakMode => $"Debugging started; paused at {DescribeLocation(dte.Debugger)}.",
            dbgDebugMode.dbgDesignMode => "Debugging started but the program already exited (or failed to launch). Check the Output window.",
            _ => $"Debugging started; the program is still running after {StartWaitMs / 1000}s (no breakpoint hit). Use vs_debug_break to pause.",
        };
    }

    public static async Task<string> StopAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            return "Not debugging; nothing to stop.";
        dte.Debugger.Stop();
        return "Debugging stopped.";
    }

    public static async Task<string> BreakAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var mode = dte.Debugger.CurrentMode;
        if (mode == dbgDebugMode.dbgDesignMode)
            return "Not debugging; nothing to break.";
        if (mode == dbgDebugMode.dbgBreakMode)
            return $"Already paused at {DescribeLocation(dte.Debugger)}.";

        dte.Debugger.Break(true);
        var final = await WaitForBreakOrExitAsync(dte, BreakWaitMs, ct);
        return final switch
        {
            dbgDebugMode.dbgBreakMode => $"Broke into the debugger at {DescribeLocation(dte.Debugger)}.",
            dbgDebugMode.dbgDesignMode => "The program exited before the break took effect.",
            _ => $"Break requested but the debugger is still running after {BreakWaitMs / 1000}s.",
        };
    }

    public static async Task<string> ContinueAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var mode = dte.Debugger.CurrentMode;
        if (mode != dbgDebugMode.dbgBreakMode)
            return $"Cannot continue: debugger is in {ModeName(mode)} mode (must be paused at a breakpoint).";

        dte.Debugger.Go(false);
        var final = await WaitForBreakOrExitAsync(dte, ContinueWaitMs, ct);
        return final switch
        {
            dbgDebugMode.dbgBreakMode => $"Continued; paused again at {DescribeLocation(dte.Debugger)}.",
            dbgDebugMode.dbgDesignMode => "Continued; the program exited.",
            _ => $"Continued; still running after {ContinueWaitMs / 1000}s (no breakpoint hit).",
        };
    }

    public static async Task<string> StepAsync(JsonNode? args, CancellationToken ct)
    {
        var action = (args?["action"]?.GetValue<string>() ?? "over").Trim().ToLowerInvariant();
        if (action is not ("into" or "over" or "out"))
            return "action must be 'into', 'over', or 'out'.";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var mode = dte.Debugger.CurrentMode;
        if (mode != dbgDebugMode.dbgBreakMode)
            return $"Cannot step: debugger is in {ModeName(mode)} mode (must be paused at a breakpoint).";

        switch (action)
        {
            case "into": dte.Debugger.StepInto(); break;
            case "over": dte.Debugger.StepOver(); break;
            case "out": dte.Debugger.StepOut(); break;
        }

        var final = await WaitForBreakOrExitAsync(dte, StepWaitMs, ct);
        return final switch
        {
            dbgDebugMode.dbgBreakMode => $"Stepped {action}; now at {DescribeLocation(dte.Debugger)}.",
            dbgDebugMode.dbgDesignMode => $"Stepped {action}; the program exited.",
            _ => $"Stepped {action}; still waiting for the debugger to pause after {StepWaitMs / 1000}s.",
        };
    }

    public static async Task<string> SetBreakpointAsync(JsonNode? args, CancellationToken ct)
    {
        var functionName = args?["functionName"]?.GetValue<string>();
        var filePath = args?["filePath"]?.GetValue<string>();
        var line = args?["line"]?.GetValue<int>() ?? 0;

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        try
        {
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                var added = dte.Debugger.Breakpoints.Add(Function: functionName);
                return $"Function breakpoint set: {functionName} ({added.Count} breakpoint(s) created).";
            }
            if (string.IsNullOrWhiteSpace(filePath) || line < 1)
                return "Provide either functionName, or filePath plus a 1-based line number.";

            var resolved = BuiltInVsTools.ResolveToolPath(filePath!);
            if (!File.Exists(resolved))
                return $"File not found: {resolved}";
            var created = dte.Debugger.Breakpoints.Add(File: resolved, Line: line);
            return $"Breakpoint set at {resolved}:{line} ({created.Count} breakpoint(s) created).";
        }
        catch (Exception ex)
        {
            return $"Could not set breakpoint: {ex.Message}";
        }
    }

    public static async Task<string> RemoveBreakpointAsync(JsonNode? args, CancellationToken ct)
    {
        var all = args?["all"]?.GetValue<bool>() ?? false;
        var functionName = args?["functionName"]?.GetValue<string>();
        var filePath = args?["filePath"]?.GetValue<string>();
        var line = args?["line"]?.GetValue<int>() ?? 0;

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        if (!all && string.IsNullOrWhiteSpace(functionName) && (string.IsNullOrWhiteSpace(filePath) || line < 1))
            return "Provide all=true, functionName, or filePath plus line.";

        string? resolved = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            resolved = BuiltInVsTools.ResolveToolPath(filePath!);
            if (line < 1)
                return "line must be a 1-based line number when filePath is given.";
        }

        // Snapshot first: Delete mutates the live collection while we enumerate.
        var breakpoints = new List<Breakpoint>();
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
            breakpoints.Add(bp);

        var removed = 0;
        foreach (var bp in breakpoints)
        {
            bool match;
            try
            {
                match = all
                    || (!string.IsNullOrWhiteSpace(functionName)
                        && (bp.FunctionName ?? string.Empty).IndexOf(functionName!, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (resolved is not null
                        && string.Equals(bp.File, resolved, StringComparison.OrdinalIgnoreCase)
                        && bp.FileLine == line);
            }
            catch (Exception)
            {
                continue; // unreadable breakpoint entry; skip rather than fail the batch
            }
            if (!match) continue;
            try
            {
                bp.Delete();
                removed++;
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"Could not delete a breakpoint: {ex.Message}");
            }
        }

        return removed == 0
            ? "No matching breakpoints found."
            : $"Removed {removed} breakpoint(s).";
    }

    public static async Task<string> ListBreakpointsAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var sb = new StringBuilder();
        var count = 0;
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
        {
            count++;
            try
            {
                var location = !string.IsNullOrEmpty(bp.File)
                    ? $"{bp.File}:{bp.FileLine}"
                    : bp.FunctionName ?? bp.Name ?? "(unknown)";
                sb.Append(location);
                if (!bp.Enabled) sb.Append(" [disabled]");
                if (!string.IsNullOrEmpty(bp.Condition)) sb.Append($" [condition: {bp.Condition}]");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.Append("(unreadable breakpoint: ").Append(ex.Message).AppendLine(")");
            }
        }
        return count == 0 ? "No breakpoints are set." : $"{count} breakpoint(s):\n{sb.ToString().TrimEnd()}";
    }

    public static async Task<string> GetStateAsync(JsonNode? args, CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var debugger = dte.Debugger;
        var mode = debugger.CurrentMode;
        var sb = new StringBuilder();
        sb.Append("Debugger mode: ").AppendLine(ModeName(mode));
        if (mode == dbgDebugMode.dbgDesignMode)
            return sb.ToString().TrimEnd();

        try { sb.Append("Process: ").AppendLine(debugger.CurrentProcess?.Name ?? "(unknown)"); }
        catch (Exception ex) { sb.Append("Process: (unavailable: ").Append(ex.Message).AppendLine(")"); }
        try { sb.Append("Thread: ").AppendLine(debugger.CurrentThread?.ID.ToString() ?? "(unknown)"); }
        catch (Exception ex) { sb.Append("Thread: (unavailable: ").Append(ex.Message).AppendLine(")"); }

        if (mode != dbgDebugMode.dbgBreakMode)
        {
            sb.Append("(Stack/locals are only readable while paused; use vs_debug_break first.)");
            return sb.ToString().TrimEnd();
        }

        sb.Append("Current location: ").AppendLine(DescribeLocation(debugger));
        try
        {
            var frames = new List<string>();
            var currentThread = debugger.CurrentThread;
            if (currentThread is not null)
            {
                foreach (StackFrame frame in currentThread.StackFrames)
                {
                    frames.Add($"  {frame.FunctionName} [{frame.Module ?? "?"}]");
                    if (frames.Count >= 10) break;
                }
            }
            sb.Append("Stack (top ").Append(frames.Count).AppendLine(" frames):");
            foreach (var f in frames) sb.AppendLine(f);
        }
        catch (Exception ex)
        {
            sb.Append("Stack: (unavailable: ").Append(ex.Message).AppendLine(")");
        }

        try
        {
            var locals = debugger.CurrentStackFrame?.Locals;
            if (locals is not null)
            {
                var shown = 0;
                sb.AppendLine("Locals (current frame):");
                foreach (Expression local in locals)
                {
                    sb.Append("  ").Append(local.Name).Append(" = ").AppendLine(Cap(local.Value, 200));
                    if (++shown >= 20)
                    {
                        sb.AppendLine("  …(locals truncated at 20)");
                        break;
                    }
                }
                if (shown == 0) sb.AppendLine("  (none)");
            }
        }
        catch (Exception ex)
        {
            sb.Append("Locals: (unavailable: ").Append(ex.Message).AppendLine(")");
        }

        return sb.ToString().TrimEnd();
    }

    public static async Task<string> EvaluateAsync(JsonNode? args, CancellationToken ct)
    {
        var expression = args?["expression"]?.GetValue<string>() ?? throw new ArgumentException("expression is required");

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";

        var mode = dte.Debugger.CurrentMode;
        if (mode != dbgDebugMode.dbgBreakMode)
            return $"Cannot evaluate: debugger is in {ModeName(mode)} mode (must be paused at a breakpoint).";

        try
        {
            var result = dte.Debugger.GetExpression(expression, false, 5000);
            if (!result.IsValidValue)
                return $"Could not evaluate '{expression}': {Cap(result.Value, 500)}";
            return $"{result.Name} = {Cap(result.Value, 2000)} (type: {result.Type})";
        }
        catch (Exception ex)
        {
            return $"Evaluation failed for '{expression}': {ex.Message}";
        }
    }

    /// <summary>
    /// Polls until the debugger leaves run mode (paused or exited) or the ceiling is reached.
    /// Delays run off the UI thread; every DTE read re-switches to it.
    /// </summary>
    private static async Task<dbgDebugMode> WaitForBreakOrExitAsync(DTE2 dte, int timeoutMs, CancellationToken ct)
    {
        var elapsed = 0;
        while (elapsed < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct).ConfigureAwait(false);
            elapsed += 200;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var mode = dte.Debugger.CurrentMode;
            if (mode != dbgDebugMode.dbgRunMode)
                return mode;
        }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        return dte.Debugger.CurrentMode;
    }

    private static string DescribeLocation(Debugger debugger)
    {
        try
        {
            var frame = debugger.CurrentStackFrame;
            if (frame is null)
                return "(no current stack frame)";
            return $"{frame.FunctionName} (module {frame.Module ?? "?"})";
        }
        catch (Exception ex)
        {
            return $"(location unavailable: {ex.Message})";
        }
    }

    private static string ModeName(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design (not debugging)",
        dbgDebugMode.dbgBreakMode => "break (paused)",
        dbgDebugMode.dbgRunMode => "run (executing)",
        _ => mode.ToString(),
    };

    private static string Cap(string? value, int max)
        => value is null || value.Length <= max ? value ?? string.Empty : value.Substring(0, max) + "…(truncated)";
}