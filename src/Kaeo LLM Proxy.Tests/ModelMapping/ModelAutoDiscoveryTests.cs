using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the contract of <see cref="ModelAutoDiscoveryService"/>: auto-managed mappings register the
/// models their upstream reports, disable models that stay gone past the grace period, and re-enable
/// them when they come back — while never overriding an operator's own "off" and never treating a
/// failed model-list read as "every model was deleted".
/// </summary>
public class ModelAutoDiscoveryTests
{
    private const string Upstream = "http://localhost:1234";

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-discovery-{Guid.NewGuid():N}.db");

    private static AppDatabase Open(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    private static ModelMapping AutoMapping(string proxyName, string upstream = Upstream) =>
        new()
        {
            ProxyName = proxyName,
            ModelName = proxyName,
            UpstreamUrl = upstream,
            AutoManaged = true,
        };

    private static UpstreamModelMetadata.FetchResult Ok(params string[] models) =>
        new([.. models], 200, "{}", null);

    private static UpstreamModelMetadata.FetchResult Failed(string error) =>
        new([], 500, null, error);

    /// <summary>A fetcher that always answers with the same result and counts its calls.</summary>
    private static ModelAutoDiscoveryService.ModelListFetcher Constant(
        UpstreamModelMetadata.FetchResult result, Action? onCall = null) =>
        (_, _, _, _) =>
        {
            onCall?.Invoke();
            return Task.FromResult(result);
        };

    // ── Sources ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_WithNoAutoManagedMappings_DoesNothingAndNeverCallsUpstream()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "manual",
            ModelName = "manual",
            UpstreamUrl = Upstream,
        });

        int calls = 0;
        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok("manual"), () => calls++));

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.False(result.Changed);
        Assert.Equal(0, result.Sources);
        Assert.Equal(0, calls);
        Assert.NotNull(service.LastScanUtc);
    }

    [Fact]
    public async Task Scan_GroupsAutoManagedMappingsByUpstream()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("alpha", "http://a:1"));
        settings.ModelMappings.Add(AutoMapping("beta", "http://a:1"));
        settings.ModelMappings.Add(AutoMapping("gamma", "http://b:2"));

        List<string> polled = [];
        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database,
            fetch: (url, _, _, _) =>
            {
                polled.Add(url);
                return Task.FromResult(Ok());
            });

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(2, result.Sources);
        Assert.Equal(2, polled.Count);
        Assert.Contains("http://a:1", polled);
        Assert.Contains("http://b:2", polled);
    }

    // ── Registration ───────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_RegistersModelsThatAppearUpstream_AsClonesOfTheAnchor()
    {
        AppSettings settings = new();
        ModelMapping anchor = AutoMapping("qwen3");
        anchor.InstructionSetName = "coding";
        anchor.Temperature = 0.2;
        anchor.CredentialName = "local";
        anchor.ContextSummarizeModelName = "summarizer";
        anchor.ContextSummarizeModelId = 7;
        settings.ModelMappings.Add(anchor);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok("qwen3", "llama3", "mistral")));

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(2, result.Registered);
        Assert.True(result.Changed);
        Assert.Equal(3, settings.ModelMappings.Count);

        ModelMapping created = settings.ModelMappings.Single(m => m.ProxyName == "llama3");
        Assert.Equal("llama3", created.ModelName);
        Assert.True(created.AutoManaged);
        Assert.True(created.IsEnabled);
        Assert.Equal("coding", created.InstructionSetName);
        Assert.Equal(0.2, created.Temperature);
        Assert.Equal("local", created.CredentialName);
        Assert.NotEqual(anchor.Id, created.Id);
        // Inheriting a compaction target would point every new model at the template's summarizer.
        Assert.Null(created.ContextSummarizeModelId);
        Assert.Null(created.ContextSummarizeModelName);
        Assert.Null(created.AutoDisabledUtc);
        Assert.Null(created.AutoMissingSinceUtc);
    }

    [Fact]
    public async Task Scan_NeverReusesAProxyNameThatIsAlreadyTaken()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("qwen3"));
        // Hand-written mapping on another upstream, not auto-managed: it still owns the routing key.
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "llama3",
            ModelName = "some-other-model",
            UpstreamUrl = "http://elsewhere:9",
        });

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok("qwen3", "llama3")));

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(0, result.Registered);
        Assert.False(result.Changed);
        Assert.Equal("some-other-model", settings.ModelMappings.Single(m => m.ProxyName == "llama3").ModelName);
    }

    [Fact]
    public async Task Scan_PersistsRegisteredModelsToTheDatabase()
    {
        string path = NewDatabasePath();
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("qwen3"));

        using (AppDatabase database = Open(path))
        {
            using ModelAutoDiscoveryService service = new(
                settings, database, fetch: Constant(Ok("qwen3", "llama3")));
            await service.RunOnceAsync();
        }

        using AppDatabase reopened = Open(path);
        IReadOnlyList<ModelMapping> loaded = reopened.LoadModelMappings();

        ModelMapping registered = Assert.Single(loaded, m => m.ProxyName == "llama3");
        Assert.True(registered.AutoManaged);
        Assert.True(registered.IsEnabled);
    }

    [Fact]
    public async Task Scan_RaisesTheMappingsChangedCallbackAndTheScanCompletedEvent()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("qwen3"));

        int callbacks = 0;
        List<ModelAutoDiscoveryResult> events = [];
        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, onMappingsChanged: () => callbacks++, fetch: Constant(Ok("qwen3", "llama3")));
        service.ScanCompleted += events.Add;

        await service.RunOnceAsync();

        Assert.Equal(1, callbacks);
        ModelAutoDiscoveryResult raised = Assert.Single(events);
        Assert.Equal(1, raised.Registered);
    }

    [Fact]
    public async Task Scan_WithNoChanges_DoesNotRaiseTheCallback()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("qwen3"));

        int callbacks = 0;
        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, onMappingsChanged: () => callbacks++, fetch: Constant(Ok("qwen3")));

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.False(result.Changed);
        Assert.Equal(0, callbacks);
    }

    // ── Grace period and disabling ─────────────────────────────────────────

    [Fact]
    public async Task Scan_MissingModel_IsNotDisabledBeforeTheGracePeriod()
    {
        DateTime now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        AppSettings settings = new();
        settings.ModelAutoDiscoveryOfflineMinutes = 10;
        ModelMapping mapping = AutoMapping("qwen3");
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok()), utcNow: () => now);

        ModelAutoDiscoveryResult first = await service.RunOnceAsync();
        Assert.Equal(0, first.Disabled);
        Assert.Equal(now, mapping.AutoMissingSinceUtc);
        Assert.True(mapping.IsEnabled);

        now = now.AddMinutes(5);
        ModelAutoDiscoveryResult second = await service.RunOnceAsync();
        Assert.Equal(0, second.Disabled);
        Assert.True(mapping.IsEnabled);
        Assert.Equal(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), mapping.AutoMissingSinceUtc);
    }

    [Fact]
    public async Task Scan_MissingModel_IsDisabledOnceTheGracePeriodElapses()
    {
        DateTime now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        AppSettings settings = new();
        settings.ModelAutoDiscoveryOfflineMinutes = 10;
        ModelMapping mapping = AutoMapping("qwen3");
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok()), utcNow: () => now);

        await service.RunOnceAsync();
        now = now.AddMinutes(11);
        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(1, result.Disabled);
        Assert.True(result.Changed);
        Assert.False(mapping.IsEnabled);
        Assert.Equal(now, mapping.AutoDisabledUtc);
        Assert.False(string.IsNullOrWhiteSpace(mapping.AutoDisabledReason));
    }

    [Fact]
    public async Task Scan_ReturningModel_IsReEnabled()
    {
        DateTime now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        AppSettings settings = new();
        ModelMapping mapping = AutoMapping("qwen3");
        mapping.IsEnabled = false;
        mapping.AutoMissingSinceUtc = now.AddHours(-1);
        mapping.AutoDisabledUtc = now.AddHours(-1);
        mapping.AutoDisabledReason = "Not present in the upstream model list since then.";
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok("qwen3")), utcNow: () => now);

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(1, result.Reactivated);
        Assert.True(mapping.IsEnabled);
        Assert.Null(mapping.AutoDisabledUtc);
        Assert.Null(mapping.AutoDisabledReason);
        Assert.Null(mapping.AutoMissingSinceUtc);
    }

    [Fact]
    public async Task Scan_NeverReEnablesAModelAnOperatorTurnedOff()
    {
        AppSettings settings = new();
        ModelMapping mapping = AutoMapping("qwen3");
        mapping.IsEnabled = false;
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok("qwen3")));

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(0, result.Reactivated);
        Assert.False(result.Changed);
        Assert.False(mapping.IsEnabled);
        Assert.Null(mapping.AutoDisabledUtc);
    }

    [Fact]
    public async Task Scan_NeverDisablesAModelAnOperatorTurnedOff()
    {
        DateTime now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        AppSettings settings = new();
        settings.ModelAutoDiscoveryOfflineMinutes = 1;
        ModelMapping mapping = AutoMapping("qwen3");
        mapping.IsEnabled = false;
        mapping.AutoMissingSinceUtc = now.AddDays(-3);
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Ok()), utcNow: () => now);

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(0, result.Disabled);
        Assert.Null(mapping.AutoDisabledUtc);
        Assert.Null(mapping.AutoDisabledReason);
    }

    // ── Failure handling ───────────────────────────────────────────────────

    [Fact]
    public async Task Scan_WithAFailedRead_ChangesNothingEvenLongPastTheGracePeriod()
    {
        DateTime now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        AppSettings settings = new();
        settings.ModelAutoDiscoveryOfflineMinutes = 1;
        ModelMapping mapping = AutoMapping("qwen3");
        mapping.AutoMissingSinceUtc = now.AddDays(-3);
        settings.ModelMappings.Add(mapping);

        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database, fetch: Constant(Failed("connection refused")), utcNow: () => now);

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.Equal(1, result.Sources);
        Assert.Equal(1, result.FailedSources);
        Assert.False(result.Changed);
        Assert.True(mapping.IsEnabled);
        Assert.Null(mapping.AutoDisabledUtc);
        Assert.Contains(result.Messages, m => m.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_AbortsWithoutWriting_WhenTheMappingListIsReplacedMidScan()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(AutoMapping("qwen3"));

        ModelMapping replacement = AutoMapping("operator-edit");
        using AppDatabase database = Open(NewDatabasePath());
        using ModelAutoDiscoveryService service = new(
            settings, database,
            fetch: (_, _, _, _) =>
            {
                // The Windows grid commits by swapping the whole list; discovery must not write a
                // stale snapshot back over that edit.
                settings.ModelMappings = [replacement];
                return Task.FromResult(Ok("qwen3", "llama3"));
            });

        ModelAutoDiscoveryResult result = await service.RunOnceAsync();

        Assert.False(result.Changed);
        Assert.Equal(0, result.Registered);
        Assert.Same(replacement, Assert.Single(settings.ModelMappings));
    }

    // ── Settings normalization ─────────────────────────────────────────────

    [Fact]
    public void Normalize_ClearsAutoBookkeeping_ForMappingsThatAreNotAutoManaged()
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "manual",
            ModelName = "manual",
            UpstreamUrl = Upstream,
            AutoManaged = false,
            AutoMissingSinceUtc = DateTime.UtcNow,
            AutoDisabledUtc = DateTime.UtcNow,
            AutoDisabledReason = "stale",
        });

        settings.Normalize();

        ModelMapping mapping = Assert.Single(settings.ModelMappings);
        Assert.Null(mapping.AutoMissingSinceUtc);
        Assert.Null(mapping.AutoDisabledUtc);
        Assert.Null(mapping.AutoDisabledReason);
    }

    [Fact]
    public void Normalize_ClampsTheAutoDiscoveryBounds()
    {
        AppSettings settings = new()
        {
            ModelAutoDiscoveryIntervalSeconds = 1,
            ModelAutoDiscoveryOfflineMinutes = 100_000,
        };

        settings.Normalize();

        Assert.Equal(AppSettings.MinModelAutoDiscoveryIntervalSeconds, settings.ModelAutoDiscoveryIntervalSeconds);
        Assert.Equal(AppSettings.MaxModelAutoDiscoveryOfflineMinutes, settings.ModelAutoDiscoveryOfflineMinutes);
    }

    [Fact]
    public void AutoDiscoveryDefaults_AreOnAndConservative()
    {
        AppSettings settings = new();

        Assert.True(settings.EnableModelAutoDiscovery);
        Assert.Equal(60, settings.ModelAutoDiscoveryIntervalSeconds);
        Assert.Equal(10, settings.ModelAutoDiscoveryOfflineMinutes);
    }
}