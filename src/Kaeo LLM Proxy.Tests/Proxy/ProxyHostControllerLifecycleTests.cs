using System.Net;
using System.Net.Sockets;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Exercises the UI-free <see cref="ProxyHostController"/> lifetime that both the Windows tray host
/// and the cross-platform headless host share: construction, proxy start/status/stop, auto-start via
/// <see cref="ProxyHostController.StartAsync"/>, idempotent shutdown, and the bind-failure event.
/// Uses the real <see cref="ProxyServer"/> (HttpListener) on an OS-probed ephemeral port.
/// </summary>
[Collection("AppLogger")]
public sealed class ProxyHostControllerLifecycleTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly ProxyHostController _controller;

    public ProxyHostControllerLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-controller-{Guid.NewGuid():N}.db");
        _settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            MaxLogEntries = 100,
            MaxConcurrentRequests = 4,
            AutoStartProxy = false,
        };
        _settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(_settings.Logging);
        ModuleHost moduleHost = new(_database, _settings);
        _controller = new ProxyHostController(_settings, _database, moduleHost);
    }

    [Fact]
    public async Task StartProxy_BindsListener_ReportsListening_ThenStops()
    {
        List<string> statuses = [];
        _controller.StatusChanged += (_, s) => statuses.Add(s);

        _controller.StartProxy();

        Assert.True(_controller.Server.IsRunning);
        Assert.Contains(statuses, s => s.StartsWith("Listening", StringComparison.Ordinal));

        await _controller.StopAsync();

        Assert.False(_controller.Server.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WithAutoStart_StartsProxy()
    {
        _settings.AutoStartProxy = true;

        await _controller.StartAsync();

        Assert.True(_controller.Server.IsRunning);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        _controller.StartProxy();
        Assert.True(_controller.Server.IsRunning);

        await _controller.StopAsync();
        await _controller.StopAsync();

        Assert.False(_controller.Server.IsRunning);
    }

    [Fact]
    public void StartProxy_OnBindFailure_RaisesProxyStartFailed_AndErrorStatus()
    {
        // An unresolvable host makes HttpListener.Start throw synchronously; the controller must
        // swallow it, log it, and surface it via events rather than crashing the host.
        _settings.ListenAddress = "no-such-host.invalid";
        Exception? captured = null;
        List<string> statuses = [];
        _controller.ProxyStartFailed += (_, ex) => captured = ex;
        _controller.StatusChanged += (_, s) => statuses.Add(s);

        _controller.StartProxy();

        Assert.NotNull(captured);
        Assert.False(_controller.Server.IsRunning);
        Assert.Contains("Error", statuses);
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        _database.Dispose();
        TryDeleteFile(_dbPath);
        TryDeleteFile(_dbPath + "-shm");
        TryDeleteFile(_dbPath + "-wal");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A locked temp database must not fail an otherwise passing test.
        }
    }
}