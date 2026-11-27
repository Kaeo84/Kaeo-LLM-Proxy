using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Endpoint tests for the dashboard's Test Console. The console talks straight to the upstream, so
/// these run against a stub SSE upstream: they pin the per-caller model filter, the 403 for a model
/// the caller's own record does not grant, and the delta/thinking/usage/done frame sequence the
/// browser parses.
/// </summary>
[Collection("AppLogger")]
public sealed class WebTestConsoleEndpointTests
{
    private const string ConsolePassword = "test console password";

    [Fact]
    public async Task Models_AdminSeesEveryEnabledMapping_AndDisabledOnesStayHidden()
    {
        await using Bundle b = await CreateBundleAsync();

        using JsonDocument doc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/testchat/models"));
        string[] names = [.. doc.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!)];

        Assert.Equal(["alpha", "beta"], names);
        Assert.DoesNotContain("gamma", names);
    }

    [Fact]
    public async Task Models_AreFilteredToTheCallersOwnGrants()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient console = await CreateConsoleUserAsync(b, "Granted Alpha", ["alpha"]);

        using JsonDocument doc = JsonDocument.Parse(await console.GetStringAsync("/api/testchat/models"));
        JsonElement models = doc.RootElement.GetProperty("models");
        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("alpha", models[0].GetProperty("name").GetString());
        // Sampling defaults travel with the model so the console can prefill its inputs.
        Assert.Equal(0.2, models[0].GetProperty("temperature").GetDouble());
        Assert.False(models[0].GetProperty("temperatureLocked").GetBoolean());
    }

    [Fact]
    public async Task Chat_ForAGrantedModel_StreamsDeltasThenDone()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient console = await CreateConsoleUserAsync(b, "Granted Alpha", ["alpha"]);

        HttpResponseMessage response = await console.PostAsJsonAsync("/api/testchat",
            new { model = "alpha", prompt = "hello there" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"delta\"", body);
        Assert.Contains("Hello", body);
        Assert.Contains(" world", body);
        Assert.Contains("\"type\":\"usage\"", body);
        Assert.Contains("\"type\":\"done\"", body);
        Assert.DoesNotContain("\"type\":\"error\"", body);

        // Exactly one chat call reaches the upstream, and the console needs no API key to make it.
        Assert.True(b.Upstream.ChatRequestCount == 1, string.Join(" | ", b.Upstream.Paths));
        Assert.Null(b.Upstream.LastChatAuthorization);
    }

    [Fact]
    public async Task Chat_ForAnUngrantedModel_IsForbidden()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient console = await CreateConsoleUserAsync(b, "Granted Alpha", ["alpha"]);

        HttpResponseMessage response = await console.PostAsJsonAsync("/api/testchat",
            new { model = "beta", prompt = "hello there" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("beta", await response.Content.ReadAsStringAsync());

        // Nothing may reach the upstream for a model the caller cannot use.
        Assert.Equal(0, b.Upstream.ChatRequestCount);
    }

    [Fact]
    public async Task Chat_ForAnUnknownModel_IsBadRequest()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.PostAsJsonAsync("/api/testchat",
            new { model = "not-a-model", prompt = "hello there" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_RequiresBothModelAndPrompt()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage noModel = await b.Client.PostAsJsonAsync("/api/testchat", new { prompt = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, noModel.StatusCode);
        Assert.Contains("Select a model", await noModel.Content.ReadAsStringAsync());

        HttpResponseMessage noPrompt = await b.Client.PostAsJsonAsync("/api/testchat", new { model = "alpha" });
        Assert.Equal(HttpStatusCode.BadRequest, noPrompt.StatusCode);
        Assert.Contains("Enter a prompt", await noPrompt.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestConsole_RequiresTheTestConsoleRole()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient readOnly = await CreateSignedInUserAsync(b, "Read Only Person", "readonly.console",
            ConsolePassword, [DashboardRoles.ReadOnly]);

        HttpResponseMessage models = await readOnly.GetAsync("/api/testchat/models");
        Assert.Equal(HttpStatusCode.Forbidden, models.StatusCode);
        string message = await models.Content.ReadAsStringAsync();
        Assert.Contains("Test Console", message);

        HttpResponseMessage chat = await readOnly.PostAsJsonAsync("/api/testchat",
            new { model = "alpha", prompt = "hi" });
        Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
    }

    [Fact]
    public async Task RolesEndpoint_IsReachableByAnySignedInSession_AndListsEveryRole()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient readOnly = await CreateSignedInUserAsync(b, "Read Only Person", "readonly.roles",
            ConsolePassword, [DashboardRoles.ReadOnly]);

        HttpResponseMessage response = await readOnly.GetAsync("/api/auth/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] keys = [.. doc.RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("key").GetString()!)];
        Assert.Equal([.. DashboardRoles.All.Select(r => r.Key)], keys);

        JsonElement testConsole = doc.RootElement.GetProperty("roles").EnumerateArray()
            .Single(r => r.GetProperty("key").GetString() == DashboardRoles.TestConsole);
        Assert.Equal("Test Console", testConsole.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(testConsole.GetProperty("description").GetString()));
        Assert.Contains("TestConsole", testConsole.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString()));
    }

    [Fact]
    public async Task Identity_ReportsEveryRoleAndTheUnionOfCapabilities()
    {
        await using Bundle b = await CreateBundleAsync();
        using HttpClient combined = await CreateSignedInUserAsync(b, "Combined Person", "combined.person",
            ConsolePassword, [DashboardRoles.UserAdmin, DashboardRoles.TestConsole]);

        using JsonDocument doc = JsonDocument.Parse(await combined.GetStringAsync("/api/auth/me"));
        Assert.Equal($"{DashboardRoles.UserAdmin},{DashboardRoles.TestConsole}",
            doc.RootElement.GetProperty("role").GetString());
        Assert.Equal(["User Admin", "Test Console"],
            doc.RootElement.GetProperty("roles").EnumerateArray()
                .Select(r => r.GetProperty("label").GetString()!));

        string[] capabilities = [.. doc.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString()!)];
        Assert.Contains("ManageUsers", capabilities);
        Assert.Contains("TestConsole", capabilities);
        Assert.Contains("ViewLogs", capabilities);
        Assert.DoesNotContain("ManageModels", capabilities);
        Assert.DoesNotContain("DashboardControl", capabilities);
    }

    /// <summary>Creates a dashboard user through the admin API and returns a signed-in client.</summary>
    private static async Task<HttpClient> CreateConsoleUserAsync(Bundle b, string name, string[] allowedModels)
        => await CreateSignedInUserAsync(b, name, $"console.{Guid.NewGuid():N}"[..24], ConsolePassword,
            [DashboardRoles.TestConsole], allowedModels);

    private static async Task<HttpClient> CreateSignedInUserAsync(
        Bundle b,
        string name,
        string username,
        string password,
        string[] roles,
        string[]? allowedModels = null)
    {
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name,
            dashboardLoginEnabled = true,
            dashboardUsername = username,
            dashboardPassword = password,
            dashboardRoles = roles,
            allowedModels = allowedModels ?? [],
        });
        created.EnsureSuccessStatusCode();

        HttpClient client = DashboardTestSession.CreateClient(b.Host.Url!, useCookies: true);
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<Bundle> CreateBundleAsync()
    {
        (StubUpstream upstream, int stubPort) = StubUpstream.StartOnFreePort();

        string dbDirectory = Path.Combine(Path.GetTempPath(), $"kaeo-testconsole-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbDirectory);
        string dbPath = Path.Combine(dbDirectory, "kaeo.db");

        var settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            AutoStartProxy = false,
            MaxLogEntries = 50,
            ModelMappings =
            [
                new ModelMapping
                {
                    ProxyName = "beta",
                    ModelName = "beta",
                    UpstreamUrl = $"http://localhost:{stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                },
                new ModelMapping
                {
                    ProxyName = "alpha",
                    ModelName = "alpha",
                    UpstreamUrl = $"http://localhost:{stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                    Temperature = 0.2,
                },
                new ModelMapping
                {
                    ProxyName = "gamma",
                    ModelName = "gamma",
                    UpstreamUrl = $"http://localhost:{stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = false,
                },
            ],
        };
        settings.Logging.ApplicationDatabasePath = dbPath;

        var database = new AppDatabase(settings.Logging);
        DashboardTestSession.SeedAdmin(database);
        var moduleHost = new ModuleHost(database, settings);
        var controller = new ProxyHostController(settings, database, moduleHost);

        settings.WebDashboard.Enabled = true;
        settings.WebDashboard.Port = GetFreePort();
        settings.WebDashboard.ListenAddress = "localhost";

        var host = new WebDashboardHost(settings, controller);
        await host.StartAsync();

        HttpClient client = DashboardTestSession.CreateClient(host.Url!, useCookies: true);
        await DashboardTestSession.SignInAdminAsync(client);

        return new Bundle
        {
            Host = host,
            Controller = controller,
            Database = database,
            Client = client,
            Upstream = upstream,
            DbPath = dbPath,
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Stub OpenAI-compatible upstream that answers with a three-chunk SSE stream.</summary>
    private sealed class StubUpstream : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private volatile string? _lastChatAuthorization;
        private int _chatRequestCount;

        /// <summary>Authorization header seen on the most recent chat completion call.</summary>
        public string? LastChatAuthorization => _lastChatAuthorization;

        /// <summary>Number of <c>/chat/completions</c> calls; other traffic (model discovery) is ignored.</summary>
        public int ChatRequestCount => Volatile.Read(ref _chatRequestCount);

        /// <summary>Every request line seen, for failure diagnostics.</summary>
        public ConcurrentQueue<string> Paths { get; } = new();

        private StubUpstream(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static (StubUpstream Server, int Port) StartOnFreePort()
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = GetFreePort();
                try { return (new StubUpstream(port), port); }
                catch (HttpListenerException) when (attempt < 4) { }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string requestLine = $"{context.Request.HttpMethod} {context.Request.RawUrl}";
                Paths.Enqueue(requestLine);
                if (requestLine.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _chatRequestCount);
                    _lastChatAuthorization = context.Request.Headers["Authorization"];
                }
                using (StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
                    await reader.ReadToEndAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                string sse =
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"}}]}\n\n" +
                    "data: {\"choices\":[{\"index\":0,\"delta\":{}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n" +
                    "data: [DONE]\n\n";
                byte[] bytes = Encoding.UTF8.GetBytes(sse);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    byte[] error = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                    await context.Response.OutputStream.WriteAsync(error);
                }
                catch { /* response already closed */ }
            }
            finally
            {
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            _listener.Close();
            _cts.Dispose();
        }
    }

    private sealed class Bundle : IAsyncDisposable
    {
        public required WebDashboardHost Host { get; init; }
        public required ProxyHostController Controller { get; init; }
        public required AppDatabase Database { get; init; }
        public required HttpClient Client { get; init; }
        public required StubUpstream Upstream { get; init; }
        public required string DbPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Controller.DisposeAsync();
            Database.Dispose();
            Upstream.Dispose();
            try
            {
                string? directory = Path.GetDirectoryName(DbPath);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A lingering file handle is not worth failing teardown over.
            }
        }
    }
}