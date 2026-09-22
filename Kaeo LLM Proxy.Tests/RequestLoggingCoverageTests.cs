using System.Net;
using System.Net.Sockets;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Proves that every request reaching the proxy's listening port produces a request-log entry
/// carrying the status the client was actually given.
/// </summary>
/// <remarks>
/// The requirement is observability: an unknown caller, an unrecognised endpoint, or a malformed
/// body has to be findable in the log afterwards. These tests bind a real <see cref="HttpListener"/>
/// on an ephemeral port and drive it over HTTP rather than calling handlers directly, because the
/// bug they guard against lived in the control flow around the handlers — branches that returned
/// before the logging <c>finally</c>, and error paths that answered the client without recording
/// what they sent. A unit test on a handler method cannot see either failure.
/// </remarks>
public sealed class RequestLoggingCoverageTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kaeo-logcov-{Guid.NewGuid():N}.db");

    private readonly AppDatabase _database;
    private readonly StatisticsService _statistics;
    private readonly ModuleHost _moduleHost;
    private readonly McpServerService _mcpServer;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _server;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AppSettings _settings = new();

    public RequestLoggingCoverageTests()
    {
        _database = new AppDatabase(new LoggingSettings { ApplicationDatabasePath = _dbPath });
        _statistics = new StatisticsService(maxEntries: 500, store: null);
        _moduleHost = new ModuleHost(_database, _settings);
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _statistics);

        _handler = new OllamaProxyHandler(_settings, _statistics, _moduleHost, _mcpServer);
        _server = new ProxyServer(_handler);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        _handler.Dispose();
        _statistics.Dispose();
        _database.Dispose();
        await _mcpServer.DisposeAsync();
    }

    /// <summary>Starts the proxy on a free loopback port and points the client at it.</summary>
    private void StartProxy()
    {
        int port = GetFreePort();
        _server.Start(port, "localhost", maxConcurrentRequests: 8);
        _client.BaseAddress = new Uri($"http://localhost:{port}");
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private RequestLog? LastLog() => _statistics.GetRecentLogs().LastOrDefault();

    /// <summary>
    /// Waits until <paramref name="expected"/> requests have been logged, then returns.
    /// </summary>
    /// <remarks>
    /// A handler writes and closes the response before <c>HandleCoreAsync</c>'s logging
    /// <c>finally</c> runs, so the client can observe the status code while the entry does not exist
    /// yet. Polling on <see cref="StatisticsService.TotalRequests"/> — which <c>AddLog</c> increments
    /// after enqueueing — makes the assertion deterministic instead of a race that passes or fails
    /// depending on scheduling. On timeout this returns silently so the assertion reports the real
    /// state rather than a cancellation exception.
    /// </remarks>
    private async Task WaitForLogsAsync(int expected)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            while (_statistics.TotalRequests < expected)
                await Task.Delay(10, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Let the caller's assertion fail with the actual observed state.
        }
    }

    /// <summary>
    /// Waits out a bounded window for a negative assertion. Nothing can be polled for when the
    /// expectation is that no entry appears, so this settles briefly instead.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(400);

    // ── Errors are logged with the code the client received ────────────────

    [Fact]
    public async Task UnknownEndpointIsLoggedAsAFourOhFourError()
    {
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/api/does-not-exist");
        await WaitForLogsAsync(1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal("/api/does-not-exist", log.OllamaPath);
        Assert.Equal(404, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
    }

    [Fact]
    public async Task UnsupportedModelManagementCallIsLoggedAsAFiveOhOneError()
    {
        StartProxy();

        HttpResponseMessage response = await _client.PostAsync("/api/pull", new StringContent("{}"));
        await WaitForLogsAsync(1);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(501, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
    }

    [Fact]
    public async Task MalformedJsonBodyIsLoggedAsAFourHundredError()
    {
        StartProxy();

        HttpResponseMessage response = await _client.PostAsync(
            "/api/chat", new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"));
        await WaitForLogsAsync(1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(400, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
    }

    // ── Successful requests stay successful ────────────────────────────────

    [Fact]
    public async Task ServedRequestIsLoggedAsASuccess()
    {
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/api/ps");
        await WaitForLogsAsync(1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(RequestStatus.Success, log.Status);
    }

    // ── Infrastructure noise follows the CollectAllTraffic switch ──────────

    [Fact]
    public async Task HealthProbeIsNotLoggedWhenNoiseCaptureIsOff()
    {
        _settings.CollectAllTraffic = false;
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/");
        await SettleAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_statistics.GetRecentLogs());
    }

    [Fact]
    public async Task HealthProbeIsLoggedWhenNoiseCaptureIsOn()
    {
        _settings.CollectAllTraffic = true;
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/");
        await WaitForLogsAsync(1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(RequestStatus.Success, log.Status);
    }

    [Fact]
    public async Task UnknownEndpointIsLoggedEvenWhenNoiseCaptureIsOff()
    {
        // The point of the fix: errors are never gated behind the noise switch, so an unrecognised
        // caller stays visible with CollectAllTraffic left at its default.
        _settings.CollectAllTraffic = false;
        StartProxy();

        await _client.GetAsync("/some/unknown/probe");
        await WaitForLogsAsync(1);

        Assert.Equal(404, LastLog()?.StatusCode);
    }

    // ── The 503 shed path ──────────────────────────────────────────────────

    [Fact]
    public async Task OverloadRejectionIsRecordedAsAFiveOhThreeError()
    {
        // ProxyServer sheds load before HandleAsync ever runs, so no RequestLog exists and the
        // logging finally never executes — the handler records the rejection directly. Exercised
        // through a real HttpListenerContext, which keeps the test deterministic; saturating the
        // actual concurrency gate would make it timing-dependent.
        using OllamaProxyHandler handler = new(_settings, _statistics, _moduleHost, _mcpServer);
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://localhost:{GetFreePort()}/");
        listener.Start();

        using HttpClient caller = new() { Timeout = TimeSpan.FromSeconds(20) };
        Task<HttpResponseMessage> call = caller.GetAsync(listener.Prefixes.First() + "overloaded");

        HttpListenerContext context = await listener.GetContextAsync();
        handler.RecordRejectedRequest(context.Request, 503, "Server at capacity.");
        context.Response.StatusCode = 503;
        context.Response.Close();

        HttpResponseMessage response = await call;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(503, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
        Assert.Contains("capacity", log.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
