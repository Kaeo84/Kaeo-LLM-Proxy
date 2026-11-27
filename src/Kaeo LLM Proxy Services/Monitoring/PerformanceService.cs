using System.Diagnostics;
using System.Runtime;
using Serilog;

namespace Kaeo.LlmProxy.Services;

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
    private int _sampling;

    /// <summary>Log GC/memory diagnostics every N samples (~60 s at 2 s interval).</summary>
    private const int DiagLogEveryNSamples = 30;

    /// <summary>CPU usage as a percentage (0–100) sampled over the last interval.</summary>
    public double CpuPercent { get; private set; }

    /// <summary>
    /// Memory used by the current process in megabytes: private bytes on Windows, resident set
    /// size elsewhere (macOS/Linux report 0 for <see cref="Process.PrivateMemorySize64"/>).
    /// </summary>
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

        if (enabled)
        {
            // Rebase the CPU baseline so the first sample after a disable gap does not average
            // the off period into the CPU percentage.
            try
            {
                _process.Refresh();
                _lastCpuTime = _process.TotalProcessorTime;
                _lastSampleTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not rebase CPU baseline when enabling performance sampling");
            }
        }

        _timer.Change(enabled ? _intervalMs : Timeout.Infinite,
                      enabled ? _intervalMs : Timeout.Infinite);

        Log.Information("Performance sampling {State}", enabled ? "enabled" : "disabled");
    }

    private void Sample(object? _)
    {
        // Skip overlapping callbacks (a stalled Process.Refresh can outlast the interval) so
        // _lastCpuTime/_lastSampleTime are only ever updated by one thread at a time.
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
            return;

        try
        {
            _process.Refresh();

            DateTime now = DateTime.UtcNow;
            TimeSpan cpuNow = _process.TotalProcessorTime;

            double elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed > 0)
            {
                double cpuUsed = (cpuNow - _lastCpuTime).TotalSeconds;
                CpuPercent = Math.Clamp(cpuUsed / (elapsed * Environment.ProcessorCount) * 100.0, 0.0, 100.0);
            }

            _lastCpuTime = cpuNow;
            _lastSampleTime = now;

            // PrivateMemorySize64 only reports on Windows; macOS and Linux return 0, so fall
            // back to the resident set size to keep the dashboard readout meaningful there.
            long memoryBytes = _process.PrivateMemorySize64;
            if (memoryBytes <= 0)
                memoryBytes = _process.WorkingSet64;

            MemoryMb = memoryBytes / (1024.0 * 1024.0);

            Gen2Collections = GC.CollectionCount(2);

            // LOH is generation 3; HeapSizeBytes covers all generations and hid LOH growth.
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            ReadOnlySpan<GCGenerationInfo> generations = gcInfo.GenerationInfo;
            LohMb = generations.Length > 3
                ? generations[3].SizeAfterBytes / (1024.0 * 1024.0)
                : gcInfo.HeapSizeBytes / (1024.0 * 1024.0);

            _sampleCount++;
            if (_sampleCount % DiagLogEveryNSamples == 0)
            {
                Log.Debug(
                    "Perf diag: Mem={MemMb:F1}MB Heap={HeapMb:F1}MB LOH={LohMb:F1}MB " +
                    "Gen0={Gen0} Gen1={Gen1} Gen2={Gen2} CPU={Cpu:F1}%",
                    MemoryMb,
                    GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                    LohMb,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    Gen2Collections,
                    CpuPercent);
            }

            try
            {
                Sampled?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "A Sampled subscriber threw; sampling continues");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: sampling can fail if the process is exiting.
            Log.Debug(ex, "Performance sampling failed; skipping this interval");
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    public void Dispose()
    {
        // Drain any in-flight timer callback before disposing the Process it samples. The wait
        // is bounded so a stuck callback cannot hang shutdown; Sample is already try/caught, so
        // even an extreme race degrades to a Debug log instead of touching a disposed Process.
        using ManualResetEvent drained = new(false);
        _timer.Dispose(drained);
        drained.WaitOne(TimeSpan.FromSeconds(2));
        _process.Dispose();
    }
}
