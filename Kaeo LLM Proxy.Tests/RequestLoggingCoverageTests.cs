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

    /// <summary>
    /// The store non-proxied requests are routed to. Kept separate from <see cref="_statistics"/> so
    /// a test can assert which log a request landed in, which is the whole point of the split.
    /// </summary>
    private readonly StatisticsService _nonProxiedStatistics;
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
        _nonProxiedStatistics = new StatisticsService(maxEntries: 500, store: null, source: LogSource.NonProxied);
        _moduleHost = new ModuleHost(_database, _settings);
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _statistics);

        _handler = new OllamaProxyHandler(_settings, _statistics, _moduleHost, _mcpServer, _nonProxiedStatistics);
        _server = new ProxyServer(_handler);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        _handler.Dispose();
        _statistics.Dispose();
        _nonProxiedStatistics.Dispose();
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

    /// <summary>Most recent entry in the Non-proxied log, or null when none was captured.</summary>
    private RequestLog? LastNonProxiedLog() => _nonProxiedStatistics.GetRecentLogs().LastOrDefault();

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

    /// <summary>
    /// Waits until <paramref name="expected"/> requests have reached the Non-proxied store, for the
    /// same response-before-finally reason as <see cref="WaitForLogsAsync"/>.
    /// </summary>
    private async Task WaitForNonProxiedLogsAsync(int expected)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            while (_nonProxiedStatistics.TotalRequests < expected)
                await Task.Delay(10, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Let the caller's assertion fail with the actual observed state.
        }
    }

    // ── Errors are logged with the code the client received ────────────────

    [Fact]
    public async Task UnknownEndpointIsLoggedAsAFourOhFourError()
    {
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/api/does-not-exist");
        await WaitForNonProxiedLogsAsync(1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        RequestLog? log = LastNonProxiedLog();
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
        await WaitForNonProxiedLogsAsync(1);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        RequestLog? log = LastNonProxiedLog();
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
        // /api/chat is a route addressed to a model, so a malformed body stays with the Proxy log
        // rather than the noise log: the client asked for a model and this is that request failing.
        RequestLog? log = LastLog();
        Assert.NotNull(log);
        Assert.Equal(400, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
    }

    // ── Successful requests stay successful ────────────────────────────────

    [Fact]
    public async Task ServedRequestIsLoggedAsASuccess()
    {
        // /api/ps is answered from local mapping config with no upstream call, so it is classified
        // as a non-proxied local stub and lands in the Non-proxied log rather than the Proxy log.
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/api/ps");
        await WaitForNonProxiedLogsAsync(1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLog? log = LastNonProxiedLog();
        Assert.NotNull(log);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(RequestStatus.Success, log.Status);
    }

    [Fact]
    public async Task NonProxiedRequestDoesNotLandInTheProxyLog()
    {
        // The separation is the point of the change: a locally-answered stub must not appear in the
        // Proxy log, or the frequent automated probes would still bury real traffic.
        StartProxy();

        await _client.GetAsync("/api/ps");
        await WaitForNonProxiedLogsAsync(1);

        Assert.Empty(_statistics.GetRecentLogs());
    }

    // ── Health-probe noise follows its own category toggle ─────

    [Fact]
    public async Task HealthProbeIsNotLoggedWhenNoiseCaptureIsOff()
    {
        // HealthProbes is a genuinely automated category and is off by default, so a 9-second
        // poller cannot bury real traffic. Both logs must stay empty.
        _settings.CollectNonProxiedCategories = [NonProxiedCategory.LocalStubs, NonProxiedCategory.RejectedRequests];
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/");
        await SettleAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_nonProxiedStatistics.GetRecentLogs());
        Assert.Empty(_statistics.GetRecentLogs());
    }

    [Fact]
    public async Task HealthProbeIsLoggedWhenNoiseCaptureIsOn()
    {
        _settings.CollectNonProxiedCategories = [.. NonProxiedCategorySet.All];
        StartProxy();

        HttpResponseMessage response = await _client.GetAsync("/");
        await WaitForNonProxiedLogsAsync(1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLog? log = LastNonProxiedLog();
        Assert.NotNull(log);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(RequestStatus.Success, log.Status);
    }

    [Fact]
    public async Task UnknownEndpointIsLoggedEvenWhenNoiseCaptureIsOff()
    {
        // The point of the fix: errors are never gated behind the noise switch, so an unrecognised
        // caller stays visible with routine noise left off.
        _settings.CollectNonProxiedCategories = [NonProxiedCategory.RejectedRequests];
        StartProxy();

        await _client.GetAsync("/some/unknown/probe");
        await WaitForNonProxiedLogsAsync(1);

        Assert.Equal(404, LastNonProxiedLog()?.StatusCode);
    }

    // ── The 503 shed path ──────────────────────────────────────────────────

    [Fact]
    public async Task OverloadRejectionIsRecordedAsAFiveOhThreeError()
    {
        // ProxyServer sheds load before HandleAsync ever runs, so no RequestLog exists and the
        // logging finally never executes — the handler records the rejection directly. Exercised
        // through a real HttpListenerContext, which keeps the test deterministic; saturating the
        // actual concurrency gate would make it timing-dependent.
        using OllamaProxyHandler handler = new(_settings, _statistics, _moduleHost, _mcpServer, _nonProxiedStatistics);
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
        RequestLog? log = LastNonProxiedLog();
        Assert.NotNull(log);
        Assert.Equal(503, log.StatusCode);
        Assert.Equal(RequestStatus.Error, log.Status);
        Assert.Contains("capacity", log.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Caller attribution is persisted and read back ──────────────────────

    [Fact]
    public void AttributionSurvivesADatabaseRoundTrip()
    {
        // The detail pane reloads a row from SQLite, so attribution must be persisted rather than
        // carried only on the in-memory summary. This also pins the column/ordinal alignment
        // between the INSERT and the two SELECTs that share ReadRequestLog: an off-by-one or a
        // column that is selected in one query but not the other fails here (or reads the wrong
        // field) instead of silently corrupting the GUI at runtime.
        DateTime timestamp = new(2026, 9, 22, 11, 42, 30, DateTimeKind.Local);

        _database.Insert(new RequestLog
        {
            Timestamp = timestamp,
            Method = "HEAD",
            OllamaPath = "/",
            StatusCode = 200,
            Status = RequestStatus.Success,
            ClientAddress = "127.0.0.1",
            UserAgent = "Visual Studio Copilot probe",
        });

        RequestLog? reloaded = _database.LoadFullLogEntry(timestamp);

        Assert.NotNull(reloaded);
        Assert.Equal("HEAD", reloaded.Method);
        Assert.Equal("127.0.0.1", reloaded.ClientAddress);
        Assert.Equal("Visual Studio Copilot probe", reloaded.UserAgent);
    }

    [Fact]
    public void AttributionIsOptionalWhenTheCallerSendsNothing()
    {
        // An older row, or a caller that sent no User-Agent, persists nulls rather than empty
        // strings (DbValue maps blank to DBNull), and the reader must return null cleanly instead
        // of throwing on the isDBNull guard. This is the path that keeps the detail dialog omitting
        // the Client/Agent lines rather than showing "null".
        DateTime timestamp = new(2026, 9, 22, 11, 42, 31, DateTimeKind.Local);

        _database.Insert(new RequestLog
        {
            Timestamp = timestamp,
            Method = "GET",
            OllamaPath = "/api/ps",
            StatusCode = 200,
            Status = RequestStatus.Success,
        });

        RequestLog? reloaded = _database.LoadFullLogEntry(timestamp);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.ClientAddress);
        Assert.Null(reloaded.UserAgent);
    }
}
