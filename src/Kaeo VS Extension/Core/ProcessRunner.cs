using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>Outcome of a child-process run. TimedOut means the process was killed at the ceiling.</summary>
internal sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Runs a child process off the UI thread with redirected output, a timeout ceiling, and
/// cancellation that kills the process. Cancellation rethrows OperationCanceledException;
/// a timeout returns the partial output with TimedOut=true. Start failures throw (caller decides
/// how to report them). net48 has no Kill(entireProcessTree)/WaitForExitAsync, so the exit is
/// awaited via the Exited event and children are expected to exit when their pipes drop.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string fileName, string arguments, string? workingDirectory, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exitTcs.TrySetResult(true);
        process.Start();
        if (process.HasExited)
            exitTcs.TrySetResult(true);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        using var registration = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    // Guarantees the async stream reads flush before we observe cancellation.
                    process.WaitForExit();
                }
            }
            catch { /* already exited or access denied */ }
            exitTcs.TrySetCanceled();
        });

        var timedOut = false;
        try
        {
            await exitTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        int exitCode;
        try { exitCode = process.ExitCode; }
        catch { exitCode = -1; }

        return new ProcessRunResult(exitCode, stdout, stderr, timedOut);
    }
}