using System.Diagnostics;
using System.Runtime;
using Serilog;

namespace Kaeo.LlmProxy.Core.Services;

/// <summary>
/// Periodically samples CPU and private-memory usage of the current process
/// and exposes them for the dashboard UI. Supports runtime enable/disable and
/// logs GC/memory diagnostics at a throttled interval.
/// </summary>
internal sealed class PerformanceService : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly System.Threading.Timer _timer;
    private readonly int _intervalMs;

    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private int _sampleCount;
    private bool _enabled;

    /// <summary>Log GC/memory diagnostics every N samples (~60 s at 2 s interval).</summary>
    private const int DiagLogEveryNSamples = 30;

    /// <summary>CPU usage as a percentage (0–100) sampled over the last interval.</summary>
    public double CpuPercent { get; private set; }

    /// <summary>Private memory set of the current process in megabytes.</summary>
    public double MemoryMb { get; private set; }

    /// <summary>Gen-2 GC collection count at the last sample.</summary>
    public int Gen2Collections { get; private set; }

    /// <summary>Large Object Heap size in megabytes at the last sample.</summary>
    public double LohMb { get; private set; }

    /// <summary>Raised on the thread-pool after each sample interval.</summary>
    public event EventHandler? Sampled;

    public PerformanceService(bool enabled = true, int intervalMs = 2000)
    {
        _intervalMs = intervalMs;
        _enabled = enabled;
        _timer = new System.Threading.Timer(Sample, null,
            enabled ? intervalMs : Timeout.Infinite,
            enabled ? intervalMs : Timeout.Infinite);
    }

    /// <summary>Enables or disables periodic sampling at runtime.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        _timer.Change(enabled ? _intervalMs : Timeout.Infinite,
                      enabled ? _intervalMs : Timeout.Infinite);

        Log.Information("Performance sampling {State}", enabled ? "enabled" : "disabled");
    }

    private void Sample(object? _)
    {
        try
        {
            _process.Refresh();

            DateTime now = DateTime.UtcNow;
            TimeSpan cpuNow = _process.TotalProcessorTime;

            double elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed > 0)
            {
                double cpuUsed = (cpuNow - _lastCpuTime).TotalSeconds;
                CpuPercent = Math.Min(100.0, cpuUsed / (elapsed * Environment.ProcessorCount) * 100.0);
            }

            _lastCpuTime = cpuNow;
            _lastSampleTime = now;

            MemoryMb = _process.PrivateMemorySize64 / (1024.0 * 1024.0);

            Gen2Collections = GC.CollectionCount(2);
            LohMb = GC.GetGCMemoryInfo().HeapSizeBytes / (1024.0 * 1024.0);

            _sampleCount++;
            if (_sampleCount % DiagLogEveryNSamples == 0)
            {
                Log.Debug(
                    "Perf diag: PrivateMem={PrivateMemMb:F1}MB Heap={HeapMb:F1}MB LOH={LohMb:F1}MB " +
                    "Gen0={Gen0} Gen1={Gen1} Gen2={Gen2} CPU={Cpu:F1}%",
                    MemoryMb,
                    GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                    LohMb,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    Gen2Collections,
                    CpuPercent);
            }

            Sampled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Non-fatal: sampling can fail if the process is exiting.
            Log.Debug(ex, "Performance sampling failed; skipping this interval");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
    }
}
