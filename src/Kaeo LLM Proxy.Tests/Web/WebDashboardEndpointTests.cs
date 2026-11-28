using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Endpoint tests for the ASP.NET Core dashboard: real Kestrel on ephemeral ports driven by an
/// HttpClient, mirroring the HttpListener E2E pattern. Covers status, incremental log polling,
/// mappings CRUD round-trip, settings validation, auth enforcement, credential secret redaction,
/// proxy control, and an HTTPS smoke test with a self-signed certificate.
/// </summary>
[Collection("AppLogger")]
public sealed class WebDashboardEndpointTests
{
    [Fact]
    public async Task Status_WhenSignedIn_ReturnsShape_AndLoginAuthMode()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("proxy", out _));
        Assert.True(doc.RootElement.TryGetProperty("dashboard", out JsonElement dash));
        Assert.Equal("login", dash.GetProperty("authMode").GetString());

        // The resolved database file is reported so two instances on different build outputs are
        // distinguishable from a bug when an edit appears to vanish.
        Assert.Equal(b.DbPath, doc.RootElement.GetProperty("databasePath").GetString());
        Assert.False(doc.RootElement.GetProperty("databaseDegraded").GetBoolean());
    }

    /// <summary>
    /// A loopback bind is reported as "localhost" so the URL handed to the browser (tray menu,
    /// Windows dashboard tab) is always clickable; a wildcard bind collapses the same way.
    /// </summary>
    [Fact]
    public async Task Url_ReportsABrowsableHost_ForLoopbackAndWildcardBinds()
    {
        await using Bundle loopback = await CreateBundleAsync(wd => wd.ListenAddress = "127.0.0.1");
        Assert.StartsWith("http://localhost:", loopback.Host.Url);

        await using Bundle wildcard = await CreateBundleAsync(wd => wd.ListenAddress = "0.0.0.0");
        Assert.StartsWith("http://localhost:", wildcard.Host.Url);
        Assert.DoesNotContain("0.0.0.0", wildcard.Host.Url);
    }

    [Fact]
    public async Task Root_ServesDashboardIndexHtml()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("js/app.js", html);
        Assert.Contains("lib/jquery-3.7.1.min.js", html);
    }

    [Fact]
    public async Task StaticAssets_AreServedFromWwwroot()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage js = await b.Client.GetAsync("/js/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage css = await b.Client.GetAsync("/css/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage jquery = await b.Client.GetAsync("/lib/jquery-3.7.1.min.js");
        Assert.Equal(HttpStatusCode.OK, jquery.StatusCode);
        Assert.Contains("jQuery", await jquery.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logs_IncrementalPoll_ReturnsEntriesThenEmptyAfterCursor()
    {
        await using Bundle b = await CreateBundleAsync();
        b.Controller.Stats.AddLog(new RequestLog
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.Now,
            Method = "POST",
            OllamaPath = "/api/chat",
            Model = "test-model",
            Status = RequestStatus.Success,
            StatusCode = 200,
        });

        using JsonDocument first = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&after=0"));
        JsonElement entries = first.RootElement.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        long next = first.RootElement.GetProperty("nextAfter").GetInt64();
        Assert.True(next > 0);

        using JsonDocument second = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        Assert.Equal(0, second.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Logs_FreshLoad_ReturnsNewestFirstFromDatabase()
    {
        await using Bundle b = await CreateBundleAsync();
        DateTime start = DateTime.Today.AddHours(9);
        for (int i = 0; i < 3; i++)
        {
            b.Database.Insert(new RequestLog
            {
                Timestamp = start.AddMinutes(i),
                Method = "POST",
                OllamaPath = "/api/chat",
                Model = $"m{i}",
                StatusCode = 200,
                Status = RequestStatus.Success,
            });
        }

        // No `after`: the answer comes from the database (the in-memory queue is empty here),
        // newest first, so a fresh view never starts at the oldest rows.
        using JsonDocument fresh = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&limit=10"));
        JsonElement[] entries = fresh.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["m2", "m1", "m0"], entries.Select(e => e.GetProperty("model").GetString()));
        long next = fresh.RootElement.GetProperty("nextAfter").GetInt64();
        // Nothing has been added to the in-memory queue, so the poll cursor sits at its start:
        // the fresh load is authoritative for everything already logged.
        Assert.Equal(0, next);

        using JsonDocument poll = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        Assert.Equal(0, poll.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Logs_Poll_DeliversARequestThatFinishedAfterNewerRowsWereAlreadySent()
    {
        await using Bundle b = await CreateBundleAsync();

        // A long request is stamped with the moment it STARTED...
        RequestLog slow = new()
        {
            RequestId = "slow",
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            Method = "POST",
            OllamaPath = "/api/chat",
            Model = "slow-model",
            Status = RequestStatus.Success,
            StatusCode = 200,
        };

        // ...while a quick request that started later finishes and is polled first, moving the
        // cursor past the long request's start time.
        b.Controller.Stats.AddLog(new RequestLog
        {
            RequestId = "quick",
            Timestamp = DateTime.UtcNow,
            Method = "POST",
            OllamaPath = "/api/chat",
            Model = "quick-model",
            Status = RequestStatus.Success,
            StatusCode = 200,
        });

        using JsonDocument first = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&after=0"));
        JsonElement firstEntries = first.RootElement.GetProperty("entries");
        Assert.Equal("quick-model", Assert.Single(firstEntries.EnumerateArray()).GetProperty("model").GetString());
        long next = first.RootElement.GetProperty("nextAfter").GetInt64();

        // The long request finally completes. Its timestamp is older than everything already sent,
        // so a timestamp cursor would drop it; the insertion cursor still hands it over.
        b.Controller.Stats.AddLog(slow);

        using JsonDocument poll = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        JsonElement polled = Assert.Single(poll.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("slow-model", polled.GetProperty("model").GetString());

        // The sequence is an internal cursor only; it must not leak into the payload.
        Assert.False(polled.TryGetProperty("logSequence", out _),
            "LogSequence must not be serialized into /api/logs.");
    }

    [Fact]
    public async Task Logs_FreshLoadCursor_DoesNotReplayEntriesAlreadyRendered()
    {
        await using Bundle b = await CreateBundleAsync();
        foreach (string model in new[] { "one", "two" })
        {
            b.Controller.Stats.AddLog(new RequestLog
            {
                RequestId = model,
                Timestamp = DateTime.UtcNow,
                Method = "POST",
                OllamaPath = "/api/chat",
                Model = model,
                Status = RequestStatus.Success,
                StatusCode = 200,
            });
        }

        using JsonDocument fresh = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&limit=10"));
        long next = fresh.RootElement.GetProperty("nextAfter").GetInt64();

        // The fresh load's cursor is the sequence high-water mark, so it is already past both
        // entries regardless of whether the persistence channel has flushed them to the database.
        Assert.True(next >= 2, $"expected the cursor to pass both entries, got {next}.");

        // The rows the fresh load just rendered must not come back on the first poll.
        using JsonDocument replay = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        Assert.Equal(0, replay.RootElement.GetProperty("entries").GetArrayLength());

        b.Controller.Stats.AddLog(new RequestLog
        {
            RequestId = "three",
            Timestamp = DateTime.UtcNow,
            Method = "POST",
            OllamaPath = "/api/chat",
            Model = "three",
            Status = RequestStatus.Success,
            StatusCode = 200,
        });

        using JsonDocument poll = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        Assert.Equal("three", Assert.Single(poll.RootElement.GetProperty("entries").EnumerateArray())
            .GetProperty("model").GetString());
    }

    [Fact]
    public async Task Logs_ReadOnly_SeesOnlyOwnRequests()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "logs owner password";
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Request Owner",
            dashboardLoginEnabled = true,
            dashboardUsername = "requests.owner",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int ownerId = createdDoc.RootElement.GetProperty("id").GetInt32();
        ProxyUser admin = b.Database.LoadProxyUsers().Single(u => u.DashboardUsername == DashboardTestSession.AdminUsername);

        DateTime start = DateTime.Today.AddHours(9);
        b.Database.Insert(new RequestLog { Timestamp = start, Method = "POST", OllamaPath = "/api/chat", Model = "mine", StatusCode = 200, Status = RequestStatus.Success, UserId = ownerId });
        b.Database.Insert(new RequestLog { Timestamp = start.AddMinutes(1), Method = "POST", OllamaPath = "/api/chat", Model = "theirs", StatusCode = 200, Status = RequestStatus.Success, UserId = admin.Id });
        b.Database.Insert(new RequestLog { Timestamp = start.AddMinutes(2), Method = "POST", OllamaPath = "/api/chat", Model = "unattributed", StatusCode = 200, Status = RequestStatus.Success });

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "requests.owner", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage fresh = new(HttpMethod.Get, "/api/logs?source=proxy");
        fresh.Headers.Add("Cookie", cookie);
        using JsonDocument freshDoc = JsonDocument.Parse(await (await b.Anonymous.SendAsync(fresh)).Content.ReadAsStringAsync());
        JsonElement mine = Assert.Single(freshDoc.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("mine", mine.GetProperty("model").GetString());

        // Incremental polls are scoped too: only the caller's attributed entries come through.
        b.Controller.Stats.AddLog(new RequestLog { Timestamp = DateTime.Now, Method = "POST", OllamaPath = "/api/chat", Model = "mine-live", StatusCode = 200, Status = RequestStatus.Success, UserId = ownerId });
        b.Controller.Stats.AddLog(new RequestLog { Timestamp = DateTime.Now.AddSeconds(1), Method = "POST", OllamaPath = "/api/chat", Model = "theirs-live", StatusCode = 200, Status = RequestStatus.Success, UserId = admin.Id });
        b.Controller.Stats.AddLog(new RequestLog { Timestamp = DateTime.Now.AddSeconds(2), Method = "POST", OllamaPath = "/api/chat", Model = "open-live", StatusCode = 200, Status = RequestStatus.Success });

        using HttpRequestMessage poll = new(HttpMethod.Get, "/api/logs?source=proxy&after=0");
        poll.Headers.Add("Cookie", cookie);
        using JsonDocument pollDoc = JsonDocument.Parse(await (await b.Anonymous.SendAsync(poll)).Content.ReadAsStringAsync());
        JsonElement live = Assert.Single(pollDoc.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("mine-live", live.GetProperty("model").GetString());

        // Admin still sees everything.
        using JsonDocument adminDoc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&after=0"));
        Assert.Equal(3, adminDoc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Logs_Detail_LoadsBodies_AndReadOnlyCannotReadOthers()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "detail owner password";
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Detail Owner",
            dashboardLoginEnabled = true,
            dashboardUsername = "detail.owner",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int ownerId = createdDoc.RootElement.GetProperty("id").GetInt32();

        DateTime mineAt = DateTime.Today.AddHours(9);
        DateTime theirsAt = mineAt.AddMinutes(5);
        b.Database.Insert(new RequestLog { Timestamp = mineAt, Method = "POST", OllamaPath = "/api/chat", Model = "mine", StatusCode = 200, Status = RequestStatus.Success, UserId = ownerId, RequestBody = "{\"mine\":true}", ResponseBody = "{\"answer\":42}" });
        b.Database.Insert(new RequestLog { Timestamp = theirsAt, Method = "POST", OllamaPath = "/api/chat", Model = "theirs", StatusCode = 200, Status = RequestStatus.Success, RequestBody = "{\"secret\":true}" });

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "detail.owner", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage own = new(HttpMethod.Get, $"/api/logs/detail?source=proxy&timestamp={Uri.EscapeDataString(mineAt.ToString("O"))}");
        own.Headers.Add("Cookie", cookie);
        HttpResponseMessage ownResponse = await b.Anonymous.SendAsync(own);
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        using JsonDocument ownDoc = JsonDocument.Parse(await ownResponse.Content.ReadAsStringAsync());
        Assert.Equal("{\"answer\":42}", ownDoc.RootElement.GetProperty("responseBody").GetString());

        using HttpRequestMessage other = new(HttpMethod.Get, $"/api/logs/detail?source=proxy&timestamp={Uri.EscapeDataString(theirsAt.ToString("O"))}");
        other.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Anonymous.SendAsync(other)).StatusCode);

        // Admin can open any entry; a bad timestamp is a 400.
        HttpResponseMessage adminDetail = await b.Client.GetAsync($"/api/logs/detail?source=proxy&timestamp={Uri.EscapeDataString(theirsAt.ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, adminDetail.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/logs/detail?source=proxy&timestamp=yesterday")).StatusCode);
    }

    [Fact]
    public async Task Logs_RequestorShowsEmailLocalPart_OrBlankWhenUnknown()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Chad Smith",
            email = "chad@example.com",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int ownerId = createdDoc.RootElement.GetProperty("id").GetInt32();

        DateTime start = DateTime.Today.AddHours(9);
        b.Database.Insert(new RequestLog { Timestamp = start, Method = "POST", OllamaPath = "/api/chat", Model = "attributed", StatusCode = 200, Status = RequestStatus.Success, UserId = ownerId });
        b.Database.Insert(new RequestLog { Timestamp = start.AddMinutes(1), Method = "POST", OllamaPath = "/api/chat", Model = "unknown", StatusCode = 200, Status = RequestStatus.Success });

        // Newest first, so the unattributed row comes before the attributed one.
        using JsonDocument doc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&limit=10"));
        JsonElement[] entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal("unknown", entries[0].GetProperty("model").GetString());
        Assert.True(!entries[0].TryGetProperty("requestor", out JsonElement unknownRequestor) || unknownRequestor.ValueKind == JsonValueKind.Null);
        Assert.Equal("attributed", entries[1].GetProperty("model").GetString());
        Assert.Equal("chad", entries[1].GetProperty("requestor").GetString());
    }

    [Fact]
    public async Task Logs_ClientTag_IsDerivedFromUserAgent_SoExtensionTrafficIsFilterable()
    {
        await using Bundle b = await CreateBundleAsync();
        DateTime start = DateTime.Today.AddHours(9);
        b.Database.Insert(new RequestLog { Timestamp = start, Method = "POST", OllamaPath = "/api/chat", Model = "from-extension", StatusCode = 200, Status = RequestStatus.Success, UserAgent = "Kaeo-VSExt/1.2.3.4" });
        b.Database.Insert(new RequestLog { Timestamp = start.AddMinutes(1), Method = "POST", OllamaPath = "/api/chat", Model = "from-copilot", StatusCode = 200, Status = RequestStatus.Success, UserAgent = "GitHubCopilotChat/0.26.7" });
        b.Database.Insert(new RequestLog { Timestamp = start.AddMinutes(2), Method = "POST", OllamaPath = "/api/chat", Model = "anonymous", StatusCode = 200, Status = RequestStatus.Success });

        using JsonDocument doc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy&limit=10"));
        Dictionary<string, JsonElement> byModel = doc.RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("model").GetString()!, StringComparer.Ordinal);

        Assert.Equal("VS extension", byModel["from-extension"].GetProperty("clientTag").GetString());
        Assert.Equal("Copilot", byModel["from-copilot"].GetProperty("clientTag").GetString());
        // No User-Agent means no tag, not a guessed one.
        Assert.True(!byModel["anonymous"].TryGetProperty("clientTag", out JsonElement none) || none.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Mappings_CrudRoundTrip_PersistsToDatabase()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
        {
            ProxyName = "proxy-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:1",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int id = createdDoc.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        using JsonDocument list = JsonDocument.Parse(await b.Client.GetStringAsync("/api/mappings"));
        Assert.Contains(list.RootElement.EnumerateArray(), e => e.GetProperty("id").GetInt32() == id);
        Assert.Contains(b.Database.LoadModelMappings(), m => m.Id == id);

        HttpResponseMessage updated = await b.Client.PutAsJsonAsync($"/api/mappings/{id}", new ModelMapping
        {
            ProxyName = "renamed",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:2",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains(b.Database.LoadModelMappings(), m => m.Id == id && m.ProxyName == "renamed");

        HttpResponseMessage deleted = await b.Client.DeleteAsync($"/api/mappings/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain(b.Database.LoadModelMappings(), m => m.Id == id);

        HttpResponseMessage missing = await b.Client.DeleteAsync($"/api/mappings/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Mappings_OwnModelsReadOnly_SeesOnlyTheModelsTheyCanCall()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "own models password";

        foreach (string name in new[] { "alpha", "beta" })
        {
            HttpResponseMessage seeded = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
            {
                ProxyName = name,
                ModelName = $"{name}-upstream",
                UpstreamUrl = "http://localhost:1",
            });
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        }

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Own Models",
            dashboardLoginEnabled = true,
            dashboardUsername = "own.models",
            dashboardPassword = password,
            dashboardRole = "ownModelsReadOnly",
            allowedModels = new[] { "alpha" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int ownerId = createdDoc.RootElement.GetProperty("id").GetInt32();

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "own.models", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage scoped = new(HttpMethod.Get, "/api/mappings");
        scoped.Headers.Add("Cookie", cookie);
        HttpResponseMessage scopedResponse = await b.Anonymous.SendAsync(scoped);
        Assert.Equal(HttpStatusCode.OK, scopedResponse.StatusCode);
        using JsonDocument scopedDoc = JsonDocument.Parse(await scopedResponse.Content.ReadAsStringAsync());
        JsonElement only = Assert.Single(scopedDoc.RootElement.EnumerateArray());
        Assert.Equal("alpha", only.GetProperty("OllamaName").GetString());

        // The full-list role and Admin still see every mapping.
        HttpResponseMessage widened = await b.Client.PutAsJsonAsync($"/api/users/{ownerId}",
            new { dashboardRole = "modelsReadOnly" });
        Assert.Equal(HttpStatusCode.OK, widened.StatusCode);

        using HttpRequestMessage full = new(HttpMethod.Get, "/api/mappings");
        full.Headers.Add("Cookie", cookie);
        using JsonDocument fullDoc = JsonDocument.Parse(await (await b.Anonymous.SendAsync(full)).Content.ReadAsStringAsync());
        Assert.Equal(2, fullDoc.RootElement.GetArrayLength());

        using JsonDocument adminDoc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/mappings"));
        Assert.Equal(2, adminDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Settings_Put_ValidatesAndApplies()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage bad = await b.Client.PutAsJsonAsync("/api/settings", new { listenPort = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        HttpResponseMessage good = await b.Client.PutAsJsonAsync("/api/settings", new { maxLogEntries = 250 });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await good.Content.ReadAsStringAsync());
        Assert.Equal(250, doc.RootElement.GetProperty("maxLogEntries").GetInt32());
        Assert.Equal(250, b.Controller.Settings.MaxLogEntries);
    }

    [Fact]
    public async Task Settings_Get_NeverExposesPassphraseOrSecrets()
    {
        await using Bundle b = await CreateBundleAsync(configureSettings: s =>
        {
            s.SecurityPassphrase = "hash:should-not-leak";
        });

        string body = await b.Client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("should-not-leak", body);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("securityPassphrase", out _));
        Assert.False(doc.RootElement.GetProperty("webDashboard").TryGetProperty("apiKey", out _));
        Assert.False(doc.RootElement.GetProperty("webDashboard").TryGetProperty("hasApiKey", out _));
    }

    [Fact]
    public async Task Settings_Get_ExposesInstructionSetNamesOnly()
    {
        await using Bundle b = await CreateBundleAsync();

        // The controller replaces Settings.InstructionSets from the database at construction, so
        // seed the live list afterwards.
        b.Controller.Settings.InstructionSets.Add(new InstructionSet
        {
            Name = "Be Terse",
            Description = "Short answers",
            Instructions = "instruction-text-must-not-leak",
        });

        string body = await b.Client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("instruction-text-must-not-leak", body);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement sets = doc.RootElement.GetProperty("instructionSets");
        JsonElement entry = default;
        foreach (JsonElement candidate in sets.EnumerateArray())
        {
            if (candidate.GetProperty("name").GetString() == "Be Terse")
                entry = candidate;
        }
        Assert.Equal(JsonValueKind.Object, entry.ValueKind);
        Assert.Equal("Short answers", entry.GetProperty("description").GetString());
        Assert.False(entry.TryGetProperty("instructions", out _));
    }

    [Fact]
    public async Task Auth_Anonymous_ApiIs401_AndPagesRedirectToLogin()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage api = await b.Anonymous.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Contains("Sign in", await api.Content.ReadAsStringAsync());

        using HttpClient noRedirect = DashboardTestSession.CreateClient(b.Host.Url!, useCookies: false, allowAutoRedirect: false);
        HttpResponseMessage root = await noRedirect.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/login.html", root.Headers.Location?.ToString());

        HttpResponseMessage appJs = await noRedirect.GetAsync("/js/app.js");
        Assert.Equal(HttpStatusCode.Redirect, appJs.StatusCode);
    }

    [Fact]
    public async Task Auth_PublicLoginAssets_AreReachableWithoutSession()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage loginPage = await b.Anonymous.GetAsync("/login.html");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        Assert.Contains("js/login.js", await loginPage.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.GetAsync("/js/login.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.GetAsync("/css/app.css")).StatusCode);

        using JsonDocument status = JsonDocument.Parse(await b.Anonymous.GetStringAsync("/api/auth/status"));
        Assert.True(status.RootElement.GetProperty("loginEnabled").GetBoolean());
        Assert.False(status.RootElement.GetProperty("setupRequired").GetBoolean());
        Assert.True(status.RootElement.GetProperty("requireLogin").GetBoolean());
    }

    [Fact]
    public async Task Auth_ProxyApiKey_NeverGrantsDashboardAccess()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage userResponse = await b.Client.PostAsJsonAsync("/api/users", new { name = "Model User" });
        Assert.Equal(HttpStatusCode.OK, userResponse.StatusCode);
        using JsonDocument userDoc = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        int userId = userDoc.RootElement.GetProperty("id").GetInt32();

        HttpResponseMessage keyResponse = await b.Client.PostAsJsonAsync($"/api/users/{userId}/keys", new { label = "laptop" });
        Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode);
        using JsonDocument keyDoc = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
        string plainText = keyDoc.RootElement.GetProperty("plainText").GetString()!;
        // The key is valid on the proxy/model plane...
        Assert.NotNull(b.Controller.Access.Authenticate(plainText));

        // ...but never on the dashboard plane, regardless of how it is presented.
        using HttpRequestMessage bearer = new(HttpMethod.Get, "/api/status");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainText);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Anonymous.SendAsync(bearer)).StatusCode);

        using HttpRequestMessage header = new(HttpMethod.Get, "/api/status");
        header.Headers.Add("X-Dashboard-Api-Key", plainText);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Anonymous.SendAsync(header)).StatusCode);

        using HttpClient noRedirect = DashboardTestSession.CreateClient(b.Host.Url!, useCookies: false, allowAutoRedirect: false);
        using HttpRequestMessage page = new(HttpMethod.Get, "/");
        page.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainText);
        HttpResponseMessage pageResponse = await noRedirect.SendAsync(page);
        Assert.Equal(HttpStatusCode.Redirect, pageResponse.StatusCode);
        Assert.Equal("/login.html", pageResponse.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Auth_FirstRunSetup_CreatesAdmin_ThenLocksOutFurtherSetup()
    {
        await using Bundle b = await CreateBundleAsync(seedAdmin: false);

        using JsonDocument before = JsonDocument.Parse(await b.Anonymous.GetStringAsync("/api/auth/status"));
        Assert.False(before.RootElement.GetProperty("loginEnabled").GetBoolean());
        Assert.True(before.RootElement.GetProperty("setupRequired").GetBoolean());

        HttpResponseMessage weak = await b.Anonymous.PostAsJsonAsync("/api/auth/setup",
            new { name = "Admin", username = "admin", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        HttpResponseMessage setup = await b.Client.PostAsJsonAsync("/api/auth/setup",
            new { name = "First Admin", username = "first.admin", password = "first admin password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using JsonDocument setupDoc = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
        Assert.Equal("admin", setupDoc.RootElement.GetProperty("role").GetString());

        // The setup response signs the browser in; the session cookie unlocks the dashboard.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/status")).StatusCode);

        HttpResponseMessage again = await b.Anonymous.PostAsJsonAsync("/api/auth/setup",
            new { name = "Second", username = "second.admin", password = "second admin password" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using JsonDocument after = JsonDocument.Parse(await b.Anonymous.GetStringAsync("/api/auth/status"));
        Assert.True(after.RootElement.GetProperty("loginEnabled").GetBoolean());
        Assert.False(after.RootElement.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task Auth_ReadOnlyLogin_CanReadLogsOnly_AndDisablingRevokesSession()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "read only password";

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Log Reader",
            dashboardLoginEnabled = true,
            dashboardUsername = "logs.reader",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int id = createdDoc.RootElement.GetProperty("id").GetInt32();

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "logs.reader", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage logsRequest = new(HttpMethod.Get, "/api/logs?source=proxy&after=0&limit=1");
        logsRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.SendAsync(logsRequest)).StatusCode);

        using HttpRequestMessage settingsRequest = new(HttpMethod.Get, "/api/settings");
        settingsRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Anonymous.SendAsync(settingsRequest)).StatusCode);

        using HttpRequestMessage mutationRequest = new(HttpMethod.Post, "/api/credentials")
        {
            Content = JsonContent.Create(new { name = "blocked", secret = "secret" }),
        };
        mutationRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Anonymous.SendAsync(mutationRequest)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/settings")).StatusCode);

        // Disabling the account revokes the live session on the very next request.
        HttpResponseMessage disabled = await b.Client.PutAsJsonAsync($"/api/users/{id}", new { isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        using HttpRequestMessage revokedRequest = new(HttpMethod.Get, "/api/logs?source=proxy&after=0&limit=1");
        revokedRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Anonymous.SendAsync(revokedRequest)).StatusCode);
    }

    private static void SeedUsage(AppDatabase database, DateTime timestamp, string model, int? userId,
        int prompt, int completion, int total) =>
        database.Insert(new RequestLog
        {
            Timestamp = timestamp,
            Method = "POST",
            OllamaPath = "/v1/chat/completions",
            Model = model,
            StatusCode = 200,
            Status = RequestStatus.Success,
            UserId = userId,
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
        });

    private static string DayString(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Usage_ReadOnly_SeesOnlyOwnUsage_EvenWhenAskingForAnotherUser()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "usage reader password";
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Usage Reader",
            dashboardLoginEnabled = true,
            dashboardUsername = "usage.reader",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int readerId = createdDoc.RootElement.GetProperty("id").GetInt32();
        ProxyUser admin = b.Database.LoadProxyUsers().Single(u => u.DashboardUsername == DashboardTestSession.AdminUsername);

        DateTime today = DateTime.Today.AddHours(9);
        SeedUsage(b.Database, today, "m1", readerId, 10, 5, 15);
        SeedUsage(b.Database, today, "m1", admin.Id, 100, 50, 150);
        SeedUsage(b.Database, today, "m2", null, 7, 3, 10);

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "usage.reader", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        // The userId parameter must be ignored for read-only sessions: they are pinned to themselves.
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/usage?from={DayString(DateTime.Today)}&to={DayString(DateTime.Today)}&userId={admin.Id}");
        request.Headers.Add("Cookie", cookie);
        HttpResponseMessage response = await b.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("self", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(15, doc.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        Assert.Empty(doc.RootElement.GetProperty("users").EnumerateArray());
        JsonElement byUser = Assert.Single(doc.RootElement.GetProperty("byUser").EnumerateArray());
        Assert.Equal(readerId, byUser.GetProperty("userId").GetInt32());
        Assert.Equal(["m1"], doc.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Usage_Admin_ScopesByUser_FiltersByModel_AndBucketsByMonth()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new { name = "Model Consumer" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int consumerId = createdDoc.RootElement.GetProperty("id").GetInt32();
        ProxyUser admin = b.Database.LoadProxyUsers().Single(u => u.DashboardUsername == DashboardTestSession.AdminUsername);

        DateTime today = DateTime.Today.AddHours(9);
        DateTime lastMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).AddHours(9);
        SeedUsage(b.Database, today, "m1", admin.Id, 100, 50, 150);
        SeedUsage(b.Database, today, "m2", consumerId, 10, 5, 15);
        SeedUsage(b.Database, today, "m1", null, 7, 3, 10);
        SeedUsage(b.Database, lastMonth, "m1", admin.Id, 30, 10, 40);

        // Default range is this week: everyone, including unattributed traffic.
        using JsonDocument all = JsonDocument.Parse(await b.Client.GetStringAsync("/api/usage"));
        Assert.Equal("all", all.RootElement.GetProperty("scope").GetString());
        Assert.Equal(175, all.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        Assert.Equal(3, all.RootElement.GetProperty("byUser").GetArrayLength());
        Assert.Contains(all.RootElement.GetProperty("byUser").EnumerateArray(),
            u => u.GetProperty("name").GetString() == "Unattributed" && u.GetProperty("totalTokens").GetInt64() == 10);
        Assert.Contains(all.RootElement.GetProperty("users").EnumerateArray(), u => u.GetProperty("id").GetInt32() == consumerId);

        using JsonDocument me = JsonDocument.Parse(await b.Client.GetStringAsync("/api/usage?userId=me"));
        Assert.Equal("self", me.RootElement.GetProperty("scope").GetString());
        Assert.Equal(150, me.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());

        using JsonDocument one = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/usage?userId={consumerId}"));
        Assert.Equal("user", one.RootElement.GetProperty("scope").GetString());
        Assert.Equal(15, one.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());

        using JsonDocument filtered = JsonDocument.Parse(await b.Client.GetStringAsync("/api/usage?model=M1"));
        Assert.Equal(160, filtered.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());

        // Two calendar months, empty spans included, oldest first.
        string url = $"/api/usage?from={DayString(lastMonth.Date)}&to={DayString(DateTime.Today)}&groupBy=month";
        using JsonDocument monthly = JsonDocument.Parse(await b.Client.GetStringAsync(url));
        JsonElement[] buckets = monthly.RootElement.GetProperty("buckets").EnumerateArray().ToArray();
        Assert.Equal(2, buckets.Length);
        Assert.Equal(lastMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture), buckets[0].GetProperty("key").GetString());
        Assert.Equal(40, buckets[0].GetProperty("totalTokens").GetInt64());
        Assert.Equal(175, buckets[1].GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, monthly.RootElement.GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task Usage_DefaultRangeIsThisWeek_AndLongerRangesAreOptIn()
    {
        await using Bundle b = await CreateBundleAsync();
        DateTime today = DateTime.Today.AddHours(9);
        // Eight days back is always outside the current (Monday-first) week.
        DateTime older = today.AddDays(-8);
        SeedUsage(b.Database, today, "m1", null, 10, 5, 15);
        SeedUsage(b.Database, older, "m1", null, 100, 50, 150);

        using JsonDocument defaults = JsonDocument.Parse(await b.Client.GetStringAsync("/api/usage"));
        Assert.Equal(DayString(today), defaults.RootElement.GetProperty("to").GetString());
        Assert.Equal(15, defaults.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());

        string url = $"/api/usage?from={DayString(older)}&to={DayString(today)}";
        using JsonDocument wider = JsonDocument.Parse(await b.Client.GetStringAsync(url));
        Assert.Equal(165, wider.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());
    }

    [Fact]
    public async Task Usage_InvalidParameters_Return400()
    {
        await using Bundle b = await CreateBundleAsync();
        string today = DayString(DateTime.Today);

        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/usage?from=not-a-date")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync($"/api/usage?from={today}&to=2020-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/usage?groupBy=year")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/usage?userId=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/usage?from=2020-01-01&to=2021-06-01")).StatusCode);
    }

    [Fact]
    public async Task Auth_RequireLoginOff_GrantsAnonymousAdmin_AndReEnableLocksAgain()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage off = await b.Client.PutAsJsonAsync("/api/settings", new { webDashboard = new { requireLogin = false } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        // The toggle applies live: anonymous callers get full admin access, pages stop redirecting.
        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.GetAsync("/api/settings")).StatusCode);
        HttpResponseMessage root = await b.Anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("js/app.js", await root.Content.ReadAsStringAsync());

        using JsonDocument me = JsonDocument.Parse(await b.Anonymous.GetStringAsync("/api/auth/me"));
        Assert.Equal("admin", me.RootElement.GetProperty("role").GetString());
        Assert.False(me.RootElement.GetProperty("loginRequired").GetBoolean());

        using JsonDocument status = JsonDocument.Parse(await b.Anonymous.GetStringAsync("/api/status"));
        Assert.Equal("open", status.RootElement.GetProperty("dashboard").GetProperty("authMode").GetString());

        // Anonymous admin can turn the requirement back on; the very next request is locked again.
        HttpResponseMessage on = await b.Anonymous.PutAsJsonAsync("/api/settings", new { webDashboard = new { requireLogin = true } });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Anonymous.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Settings_Put_PersistsRuntimeSettings_ToDatabase()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.PutAsJsonAsync("/api/settings", new
        {
            allowMultipleInstances = false,
            autoStartProxy = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("allowMultipleInstances").GetBoolean());

        // Runtime settings live in the database, not settings.jsonc: they must survive a reload.
        RuntimeSettings persisted = b.Database.LoadRuntimeSettings();
        Assert.False(persisted.AllowMultipleInstances);
        Assert.False(persisted.AutoStartProxy);
    }

    [Fact]
    public async Task Settings_Put_RoundTripsTheModelAutoDiscoveryKnobs()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.PutAsJsonAsync("/api/settings", new
        {
            enableModelAutoDiscovery = false,
            modelAutoDiscoveryIntervalSeconds = 900,
            modelAutoDiscoveryOfflineMinutes = 45,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("enableModelAutoDiscovery").GetBoolean());
        Assert.Equal(900, doc.RootElement.GetProperty("modelAutoDiscoveryIntervalSeconds").GetInt32());
        Assert.Equal(45, doc.RootElement.GetProperty("modelAutoDiscoveryOfflineMinutes").GetInt32());

        RuntimeSettings persisted = b.Database.LoadRuntimeSettings();
        Assert.False(persisted.EnableModelAutoDiscovery);
        Assert.Equal(900, persisted.ModelAutoDiscoveryIntervalSeconds);
        Assert.Equal(45, persisted.ModelAutoDiscoveryOfflineMinutes);
        Assert.Equal(900, b.Controller.Settings.ModelAutoDiscoveryIntervalSeconds);
    }

    [Fact]
    public async Task Settings_Put_RejectsAnOutOfRangeDiscoveryInterval()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.PutAsJsonAsync("/api/settings",
            new { modelAutoDiscoveryIntervalSeconds = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(60, b.Controller.Settings.ModelAutoDiscoveryIntervalSeconds);
    }

    [Fact]
    public async Task Credentials_PostThenGet_NeverLeaksSecretMaterial()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage posted = await b.Client.PostAsJsonAsync("/api/credentials", new
        {
            name = "cred-1",
            description = "test credential",
            secret = "SUPER-SECRET-VALUE",
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        string body = await b.Client.GetStringAsync("/api/credentials");
        Assert.DoesNotContain("SUPER-SECRET-VALUE", body);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement cred = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "cred-1");
        Assert.True(cred.GetProperty("hasSecretMaterial").GetBoolean());
        Assert.False(cred.TryGetProperty("secret", out _));
        Assert.False(cred.TryGetProperty("privateKey", out _));

        HttpResponseMessage deleted = await b.Client.DeleteAsync("/api/credentials/cred-1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Proxy_StartStop_TogglesIsRunning()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage started = await b.Client.PostAsync("/api/proxy/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using JsonDocument startDoc = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        Assert.True(startDoc.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.True(b.Controller.Server.IsRunning);

        HttpResponseMessage stopped = await b.Client.PostAsync("/api/proxy/stop", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        using JsonDocument stopDoc = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
        Assert.False(stopDoc.RootElement.GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task McpAndModules_ReturnOk()
    {
        await using Bundle b = await CreateBundleAsync();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/stats")).StatusCode);
    }

    [Fact]
    public async Task WebTraffic_RecordsDashboardCalls_AndSkipsStatusPolling()
    {
        await using Bundle b = await CreateBundleAsync();

        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/status")).StatusCode);

        RequestLog entry = await WaitForWebTrafficAsync(b, "/api/credentials");
        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal(RequestStatus.Success, entry.Status);

        // The dashboard polls /api/status continuously; recording it would bury real traffic.
        Assert.DoesNotContain(b.Controller.WebStats.GetRecentLogs(), log => log.OllamaPath == "/api/status");

        // Web traffic shares the request-log store, under its own table.
        await b.Controller.WebStats.FlushAsync();
        Assert.NotNull(b.Database.LoadFullLogEntry(entry.Timestamp, LogSource.Web));
    }

    [Fact]
    public async Task WebTraffic_RecordsRejectedRequests_EvenThoughTheyArePollingEndpoints()
    {
        await using Bundle b = await CreateBundleAsync();

        // Anonymous polling is rejected before the auth gate, so the skip list must not apply:
        // an unauthorized caller is exactly the traffic worth seeing.
        HttpResponseMessage rejected = await b.Anonymous.GetAsync("/api/logs");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        RequestLog entry = await WaitForWebTrafficAsync(b, "/api/logs");
        Assert.Equal(401, entry.StatusCode);
        Assert.Equal(RequestStatus.Error, entry.Status);
    }

    /// <summary>
    /// The traffic middleware records after the response is written, so the entry can land a
    /// moment after the client's call returns. Polls briefly instead of sleeping a fixed time.
    /// </summary>
    private static async Task<RequestLog> WaitForWebTrafficAsync(Bundle bundle, string path)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            RequestLog? match = bundle.Controller.WebStats.GetRecentLogs()
                .FirstOrDefault(log => log.OllamaPath == path);
            if (match is not null)
                return match;

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"No web traffic entry was recorded for '{path}'.");
    }

    [Fact]
    public async Task Proxy_Restart_KeepsIsRunning()
    {
        await using Bundle b = await CreateBundleAsync();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PostAsync("/api/proxy/start", null)).StatusCode);

        HttpResponseMessage restarted = await b.Client.PostAsync("/api/proxy/restart", null);
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await restarted.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.True(b.Controller.Server.IsRunning);
    }

    [Fact]
    public async Task Mcp_Stop_ReturnsStateShape_EvenWhenNothingIsRunning()
    {
        await using Bundle b = await CreateBundleAsync();
        Assert.False(b.Controller.McpServer.IsRunning);

        HttpResponseMessage stopped = await b.Client.PostAsync("/api/mcp/stop", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Stopped", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("endpointUrl", out _));
    }

    [Fact]
    public async Task Streams_ReturnEmptyCollections_WhenNothingIsInFlight()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.GetAsync("/api/streams");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("live").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("history").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/streams/detail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync("/api/streams/detail?id=missing")).StatusCode);
    }

    [Fact]
    public async Task SysLog_ReturnsShape_AndRejectsUnknownDetail()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.GetAsync("/api/syslog?limit=50&hideHeartbeats=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("dbHealthy", out _));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("entries").ValueKind);
    }

    [Fact]
    public async Task ObservabilityEndpoints_AreForbiddenForReadOnlySessions()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "read only password";

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Stream Watcher",
            dashboardLoginEnabled = true,
            dashboardUsername = "streams.reader",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "streams.reader", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        // The log store can hold payload text and the stream monitor exposes other users' bytes.
        foreach (string path in new[] { "/api/syslog", "/api/streams", "/api/streams/detail?id=x" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.Anonymous.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Mappings_WebMutation_RaisesModelMappingsSaved_SoTheWindowsGridResyncs()
    {
        await using Bundle b = await CreateBundleAsync();
        int raised = 0;
        b.Database.ModelMappingsSaved += () => Interlocked.Increment(ref raised);

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
        {
            ProxyName = "web-added",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:1",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // The Windows GUI rebuilds the mapping list from its own grid rows, so it must be told.
        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.Contains(b.Controller.Settings.ModelMappings, m => m.ProxyName == "web-added");
        Assert.Contains(b.Database.LoadModelMappings(), m => m.ProxyName == "web-added");
    }

    [Fact]
    public async Task MappingsDiscovery_ReportsTheSettingsAndTheLastScan()
    {
        await using Bundle b = await CreateBundleAsync(configureSettings: s =>
        {
            s.EnableModelAutoDiscovery = false;
            s.ModelAutoDiscoveryIntervalSeconds = 300;
            s.ModelAutoDiscoveryOfflineMinutes = 30;
        });

        using JsonDocument doc = JsonDocument.Parse(await b.Client.GetStringAsync("/api/mappings/discovery"));
        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(300, root.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(30, root.GetProperty("offlineMinutes").GetInt32());
        Assert.False(root.GetProperty("isRunning").GetBoolean());
        // ASP.NET Core omits nulls from JSON, so "never scanned" shows up as an absent property.
        Assert.False(root.TryGetProperty("lastScanUtc", out JsonElement lastScan));
        Assert.Equal(JsonValueKind.Undefined, lastScan.ValueKind);
        Assert.False(root.TryGetProperty("lastResult", out _));
    }

    [Fact]
    public async Task MappingsDiscover_WithNoAutoManagedMappings_ReturnsAnEmptyResultAndRecordsTheScan()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage response = await b.Client.PostAsync("/api/mappings/discover", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("sources").GetInt32());
        Assert.False(doc.RootElement.GetProperty("changed").GetBoolean());
        Assert.NotNull(b.Controller.ModelDiscovery.LastScanUtc);

        using JsonDocument status = JsonDocument.Parse(await b.Client.GetStringAsync("/api/mappings/discovery"));
        Assert.True(status.RootElement.TryGetProperty("lastResult", out JsonElement lastResult));
        Assert.Equal(0, lastResult.GetProperty("sources").GetInt32());
    }

    [Fact]
    public async Task Mappings_WebSave_ClearsDiscoveryBookkeeping_SoAManualEditWins()
    {
        await using Bundle b = await CreateBundleAsync();
        DateTime now = DateTime.UtcNow;

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
        {
            ProxyName = "auto-model",
            ModelName = "auto-model",
            UpstreamUrl = "http://localhost:1",
            AutoManaged = true,
            AutoMissingSinceUtc = now.AddHours(-2),
            AutoDisabledUtc = now.AddHours(-1),
            AutoDisabledReason = "gone upstream",
            IsEnabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        ModelMapping stored = b.Controller.Settings.ModelMappings.Single(m => m.ProxyName == "auto-model");
        Assert.True(stored.AutoManaged);
        Assert.Null(stored.AutoMissingSinceUtc);
        Assert.Null(stored.AutoDisabledUtc);
        Assert.Null(stored.AutoDisabledReason);

        // Turning auto-managed off must drop the markers too, or the service would keep claiming it
        // disabled a model the operator has since taken over.
        HttpResponseMessage updated = await b.Client.PutAsJsonAsync($"/api/mappings/{stored.Id}", new ModelMapping
        {
            ProxyName = "auto-model",
            ModelName = "auto-model",
            UpstreamUrl = "http://localhost:1",
            AutoManaged = false,
            AutoMissingSinceUtc = now.AddHours(-2),
            AutoDisabledUtc = now.AddHours(-1),
            AutoDisabledReason = "gone upstream",
            IsEnabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        ModelMapping after = b.Controller.Settings.ModelMappings.Single(m => m.ProxyName == "auto-model");
        Assert.False(after.AutoManaged);
        Assert.Null(after.AutoMissingSinceUtc);
        Assert.Null(after.AutoDisabledUtc);
        Assert.Null(after.AutoDisabledReason);
    }

    [Fact]
    public async Task Mappings_WebSave_KeepsDiscoveryBookkeeping_WhenNothingAboutItChanged()
    {
        await using Bundle b = await CreateBundleAsync();
        DateTime disabledAt = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
        {
            ProxyName = "auto-model",
            ModelName = "auto-model",
            UpstreamUrl = "http://localhost:1",
            AutoManaged = true,
            IsEnabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        ModelMapping stored = b.Controller.Settings.ModelMappings.Single(m => m.ProxyName == "auto-model");

        // Simulate what discovery had written before the tab was opened and saved again.
        stored.IsEnabled = false;
        stored.AutoMissingSinceUtc = disabledAt;
        stored.AutoDisabledUtc = disabledAt;
        stored.AutoDisabledReason = "gone upstream";

        HttpResponseMessage updated = await b.Client.PutAsJsonAsync($"/api/mappings/{stored.Id}", new ModelMapping
        {
            ProxyName = "auto-model",
            ModelName = "auto-model",
            UpstreamUrl = "http://localhost:1",
            AutoManaged = true,
            AutoMissingSinceUtc = disabledAt,
            AutoDisabledUtc = disabledAt,
            AutoDisabledReason = "gone upstream",
            IsEnabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        ModelMapping after = b.Controller.Settings.ModelMappings.Single(m => m.ProxyName == "auto-model");
        Assert.Equal(disabledAt, after.AutoDisabledUtc);
        Assert.Equal("gone upstream", after.AutoDisabledReason);
    }

    [Fact]
    public async Task Https_WithSelfSignedCertificate_ServesStatus()
    {
        string certPath = Path.Combine(Path.GetTempPath(), $"kaeo-web-{Guid.NewGuid():N}.pfx");
        using (RSA rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            await File.WriteAllBytesAsync(certPath, cert.Export(X509ContentType.Pfx, "test-pass"));
        }

        try
        {
            await using Bundle b = await CreateBundleAsync(wd =>
            {
                wd.Scheme = "Https";
                wd.CertificatePath = certPath;
                wd.CertificatePassword = "test-pass";
            }, certPath: certPath);

            Assert.StartsWith("https://", b.Host.Url);
            HttpResponseMessage response = await b.Client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TryDelete(certPath);
        }
    }

    [Fact]
        public async Task Https_WithCertificateFromCredential_ServesStatus()
        {
            string pfxBase64;
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
                pfxBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, "cred-pass"));
            }

            await using Bundle b = await CreateBundleAsync(
                wd =>
                {
                    wd.Scheme = "Https";
                    wd.CertificateCredentialName = "dashboard-cert";
                },
                settings => settings.Credentials =
                [
                    new StoredCredential
                    {
                        Name = "dashboard-cert",
                        Description = "Dashboard HTTPS",
                        Secret = "cred-pass",
                        Certificate = pfxBase64,
                    },
                ]);

            Assert.StartsWith("https://", b.Host.Url);
            Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/status")).StatusCode);
        }

        private static async Task<Bundle> CreateBundleAsync(
        Action<WebDashboardSettings>? configureDashboard = null,
        Action<AppSettings>? configureSettings = null,
        string? certPath = null,
        bool seedAdmin = true)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-web-{Guid.NewGuid():N}.db");
        var settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            AutoStartProxy = false,
            MaxLogEntries = 100,
        };
        settings.Logging.ApplicationDatabasePath = dbPath;
        configureSettings?.Invoke(settings);

        var database = new AppDatabase(settings.Logging);
        if (seedAdmin)
            DashboardTestSession.SeedAdmin(database);
        var moduleHost = new ModuleHost(database, settings);
        var controller = new ProxyHostController(settings, database, moduleHost);

        settings.WebDashboard.Enabled = true;
        settings.WebDashboard.Port = GetFreePort();
        settings.WebDashboard.ListenAddress = "localhost";
        configureDashboard?.Invoke(settings.WebDashboard);

        var host = new WebDashboardHost(settings, controller);
        await host.StartAsync();

        HttpClient client = DashboardTestSession.CreateClient(host.Url!, useCookies: true);
        if (seedAdmin)
            await DashboardTestSession.SignInAdminAsync(client);

        return new Bundle
        {
            Host = host,
            Controller = controller,
            Database = database,
            Client = client,
            Anonymous = DashboardTestSession.CreateClient(host.Url!, useCookies: false),
            DbPath = dbPath,
            CertPath = certPath,
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp artifacts.
        }
    }

    private sealed class Bundle : IAsyncDisposable
    {
        public required WebDashboardHost Host { get; init; }
        public required ProxyHostController Controller { get; init; }
        public required AppDatabase Database { get; init; }
        public required HttpClient Client { get; init; }
        public required HttpClient Anonymous { get; init; }
        public required string DbPath { get; init; }
        public string? CertPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Anonymous.Dispose();
            await Host.DisposeAsync();
            await Controller.DisposeAsync();
            Database.Dispose();
            TryDelete(DbPath);
            TryDelete(DbPath + "-shm");
            TryDelete(DbPath + "-wal");
            if (CertPath is not null)
                TryDelete(CertPath);
        }
    }
}