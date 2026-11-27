using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Guards the heartbeat monitor against being torn down while one of its periodic probes is still
/// in flight, which is what happens whenever the operator saves settings and a model that was
/// being probed is disabled, renamed or removed.
/// </summary>
/// <remarks>
/// The regression this covers surfaced as a fatal crash report rather than a failed assertion:
/// <c>System.Threading.Timer.Dispose</c> does not join a callback that is already running, so
/// disposal disposed the send semaphore out from under the probe, the probe threw
/// <see cref="ObjectDisposedException"/> from its <c>finally</c>, and because the timer had fired
/// the send fire-and-forget the exception was never observed. It resurfaced on the finalizer
/// thread as "Unhandled exception (Unobserved Task)".
/// <para>
/// That failure cannot be seen by calling handler methods directly, so this test stands up a real
/// upstream that deliberately stalls mid-probe, disposes the monitor underneath it, and then
/// asserts nothing escapes to <see cref="TaskScheduler.UnobservedTaskException"/>.
/// </para>
/// </remarks>
[Collection("AppLogger")]
public sealed class HeartbeatMonitorDisposalTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kaeo-heartbeat-{Guid.NewGuid():N}.db");

    private readonly StalledUpstream _upstream;
    private readonly int _upstreamPort;
    private readonly AppDatabase _database;
    private readonly StatisticsService _statistics;
    private readonly McpServerService _mcpServer;
    private readonly ModuleHost _moduleHost;
    private readonly OllamaProxyHandler _handler;

    public HeartbeatMonitorDisposalTests()
    {
        _upstreamPort = GetFreePort();
        _upstream = new StalledUpstream(_upstreamPort);

        AppSettings settings = new()
        {
            HeartbeatIntervalSeconds = AppSettings.MaxHeartbeatIntervalSeconds,
            ModelMappings =
            [
                new ModelMapping
                {
                    ProxyName = "stalled-model",
                    ModelName = "stalled-model",
                    UpstreamUrl = $"http://localhost:{_upstreamPort}",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                    EnableHeartbeats = true,
                },
            ],
        };
        settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(settings.Logging);
        _statistics = new StatisticsService(maxEntries: 50);
        _moduleHost = new ModuleHost(_database, settings);
        _mcpServer = new McpServerService(_database, settings, _moduleHost, _statistics, new ProxyAccessService(_database));
        _handler = new OllamaProxyHandler(settings, _statistics, _moduleHost, _mcpServer);
    }

    [Fact]
    public async Task StoppingMonitorsWhileAProbeIsInFlight_DoesNotRaiseUnobservedTaskException()
    {
        ConcurrentBag<string> escaped = [];
        void observer(object? _, UnobservedTaskExceptionEventArgs e)
        {
            escaped.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += observer;
        try
        {
            _handler.StartHeartbeatMonitors();

            // The monitor fires immediately on start, so this proves the probe is genuinely
            // in flight — parked inside the upstream — before the monitor is disposed.
            await _upstream.WaitForProbeAsync();

            Stopwatch stopwatch = Stopwatch.StartNew();
            _handler.StopHeartbeatMonitors();
            stopwatch.Stop();

            // Disposal must stay responsive: it runs on the UI thread when settings are saved.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"StopHeartbeatMonitors took {stopwatch.Elapsed}, which would stall the settings UI.");

            // Let the probe unwind against the now-disposed monitor, then collect the discarded
            // task so any unobserved exception reaches the observer above.
            _upstream.ReleaseProbe();
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(
                escaped.IsEmpty,
                "Disposing the monitor let an exception escape unobserved: "
                    + string.Join(" | ", escaped));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= observer;
            _handler.StopHeartbeatMonitors();
        }
    }

    /// <summary>
    /// An upstream that accepts the probe and then holds the response until released, so the test
    /// can dispose the monitor at a point where the send is provably still running.
    /// </summary>
    private sealed class StalledUpstream : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _probeArrived = new(0);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StalledUpstream(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public Task WaitForProbeAsync() => _probeArrived.WaitAsync(TimeSpan.FromSeconds(15));

        public void ReleaseProbe() => _release.TrySetResult();

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                _probeArrived.Release();

                // Park until the test has disposed the monitor. Bounded so a broken test cannot
                // leak this handler forever.
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("""{"data":[]}""");
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            }
            catch
            {
                // The probe was cancelled, or the listener stopped: both are expected here.
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _release.TrySetResult();
            try { _listener.Stop(); _listener.Close(); } catch { }
            _cts.Dispose();
            _probeArrived.Dispose();
        }
    }

    private static int GetFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _handler.Dispose();
        _statistics.Dispose();
        _database.Dispose();
        _upstream.Dispose();

        TryDeleteFile(_dbPath);
        TryDeleteFile(_dbPath + "-shm");
        TryDeleteFile(_dbPath + "-wal");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}