using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>Outcome of a single model auto-discovery scan, for the dashboard and the Windows GUI.</summary>
/// <param name="Sources">Distinct upstreams scanned.</param>
/// <param name="FailedSources">Upstreams whose model list could not be read; their mappings were left untouched.</param>
/// <param name="Registered">Mappings created for models that appeared upstream.</param>
/// <param name="Reactivated">Mappings discovery had disabled that came back.</param>
/// <param name="Disabled">Mappings disabled because the model stayed missing past the grace period.</param>
/// <param name="Messages">Per-source notes (errors and state changes) for display.</param>
internal sealed record ModelAutoDiscoveryResult(
    int Sources,
    int FailedSources,
    int Registered,
    int Reactivated,
    int Disabled,
    IReadOnlyList<string> Messages)
{
    public static ModelAutoDiscoveryResult None { get; } = new(0, 0, 0, 0, 0, []);

    public bool Changed => Registered > 0 || Reactivated > 0 || Disabled > 0;
}

/// <summary>
/// Keeps <see cref="ModelMapping.AutoManaged"/> mappings in step with their upstream's
/// <c>/v1/models</c> list: registers models that appear, and disables models that stay gone past a
/// configurable grace period. A model that comes back is re-enabled, but only when discovery was the
/// thing that disabled it — an operator's own "off" is never overridden.
/// </summary>
/// <remarks>
/// Every auto-managed mapping doubles as a discovery <em>source</em>: its upstream URL, credential
/// and upstream type identify one endpoint to poll, so a new machine only needs a single hand-written
/// auto-managed mapping before the rest of its models register themselves.
/// <para>
/// Only a <em>successful</em> model-list read can disable anything. A failed read (upstream down,
/// timeout, auth error) leaves every mapping exactly as it was, so an outage of the whole server never
/// looks like "all the models were deleted".
/// </para>
/// <para>
/// Newly registered models are clones of the first auto-managed mapping in their source, which is what
/// makes one configured model a useful template for the rest: instruction set, sampling defaults,
/// reasoning effort and credential all carry over. Compaction targets are deliberately cleared, since
/// copying one would point several models at the same summarizer (or at the template itself).
/// </para>
/// </remarks>
internal sealed class ModelAutoDiscoveryService : IDisposable
{
    /// <summary>
    /// Signature of the upstream model-list call, defaulted to
    /// <see cref="UpstreamModelMetadata.FetchModelsAsync"/> and overridable so tests can drive the
    /// reconciliation without a live server.
    /// </summary>
    internal delegate Task<UpstreamModelMetadata.FetchResult> ModelListFetcher(
        string upstreamUrl, string? apiKey, int timeoutSeconds, CancellationToken cancellationToken);

    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly ModelListFetcher _fetch;
    private readonly Func<DateTime> _utcNow;
    private readonly Action? _onMappingsChanged;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _disposed;

    /// <summary>Raised after a scan that changed the mapping list, on the scanning thread.</summary>
    public event Action<ModelAutoDiscoveryResult>? ScanCompleted;

    /// <summary>UTC time of the last completed scan, or null when none has run.</summary>
    public DateTime? LastScanUtc { get; private set; }

    /// <summary>Result of the last completed scan, or null when none has run.</summary>
    public ModelAutoDiscoveryResult? LastResult { get; private set; }

    /// <summary>True while the background scan loop is running.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    public ModelAutoDiscoveryService(
        AppSettings settings,
        AppDatabase database,
        Action? onMappingsChanged = null,
        ModelListFetcher? fetch = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);

        _settings = settings;
        _database = database;
        _onMappingsChanged = onMappingsChanged;
        _fetch = fetch ?? UpstreamModelMetadata.FetchModelsAsync;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Starts the background scan loop. Idempotent: a second call while it is running does nothing.
    /// The loop reads its interval and master switch from settings on every iteration, so changing
    /// them in either GUI takes effect without a restart.
    /// </summary>
    public void Start()
    {
        if (_disposed || IsRunning)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, _settings.ModelAutoDiscoveryIntervalSeconds)), token)
                        .ConfigureAwait(false);

                    if (!_settings.EnableModelAutoDiscovery)
                        continue;

                    await RunOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The loop must outlive any single failure: a bad upstream or a locked database
                    // would otherwise silently stop auto-discovery for the life of the process.
                    Log.Warning(ex, "Model auto-discovery scan failed");
                }
            }
        }, token);

        Log.Information(
            "Model auto-discovery started. IntervalSeconds={Interval} OfflineGraceMinutes={Grace}",
            _settings.ModelAutoDiscoveryIntervalSeconds,
            _settings.ModelAutoDiscoveryOfflineMinutes);
    }

    /// <summary>Stops the background scan loop and waits for an in-flight scan to finish.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is null)
            return;

        try
        {
            cts.Cancel();
            if (loop is not null)
                await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error while stopping model auto-discovery");
        }
        finally
        {
            cts.Dispose();
        }

        Log.Information("Model auto-discovery stopped");
    }

    /// <summary>
    /// Runs one scan synchronously and returns what it did. Safe to call from a request handler
    /// ("Discover now") while the background loop is running: scans are serialised, and the second
    /// caller simply waits its turn.
    /// </summary>
    public async Task<ModelAutoDiscoveryResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ModelAutoDiscoveryResult result = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            LastScanUtc = _utcNow();
            LastResult = result;
            return result;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<ModelAutoDiscoveryResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        List<DiscoverySource> sources;
        List<ModelMapping> snapshot;
        lock (AppSettings.ModelMappingsSyncRoot)
        {
            snapshot = _settings.ModelMappings;
            sources = BuildSources(snapshot);
        }

        if (sources.Count == 0)
            return ModelAutoDiscoveryResult.None;

        int failedSources = 0;
        int registered = 0;
        int reactivated = 0;
        int disabled = 0;
        bool changed = false;
        List<string> messages = [];

        foreach (DiscoverySource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UpstreamModelMetadata.FetchResult fetch = await _fetch(
                source.UpstreamUrl,
                _settings.ResolveApiKey(source.Anchor),
                _settings.ModelDiscoveryTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (fetch.ErrorMessage is not null)
            {
                // A failed read proves nothing about which models exist, so it must not disable
                // anything. Report it and move on to the next source.
                failedSources++;
                messages.Add($"{source.UpstreamUrl}: {fetch.ErrorMessage}");
                Log.Debug(
                    "Model auto-discovery could not read the model list from {Upstream}: {Error}",
                    source.UpstreamUrl, fetch.ErrorMessage);
                continue;
            }

            HashSet<string> present = new(fetch.Models, StringComparer.OrdinalIgnoreCase);

            lock (AppSettings.ModelMappingsSyncRoot)
            {
                if (!ReferenceEquals(_settings.ModelMappings, snapshot))
                {
                    // A GUI replaced the list between snapshot and now (the Windows grid commits by
                    // swapping the whole list). Skip the rest of this scan rather than writing a
                    // stale list back over the operator's edit; the next scan starts from the new one.
                    messages.Add("Scan skipped: the model list changed while it was running.");
                    Log.Debug("Model auto-discovery aborted: the mapping list was replaced mid-scan");
                    break;
                }

                ReconcileOutcome outcome = ReconcileSource(source, present, snapshot, messages);
                registered += outcome.Registered;
                reactivated += outcome.Reactivated;
                disabled += outcome.Disabled;
                changed |= outcome.Changed;
            }
        }

        ModelAutoDiscoveryResult result = new(
            sources.Count, failedSources, registered, reactivated, disabled, messages);

        if (changed)
        {
            try
            {
                // SaveModelMappings raises ModelMappingsSaved, which re-syncs the Windows grid.
                _database.SaveModelMappings(snapshot);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                // Sharing violation with another instance: the in-memory change stands and the next
                // scan retries the write.
                Log.Warning(ex, "Could not persist auto-discovered model mappings");
            }

            Log.Information(
                "Model auto-discovery: registered {Registered}, reactivated {Reactivated}, disabled {Disabled}",
                registered, reactivated, disabled);

            try
            {
                _onMappingsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error while applying auto-discovered model mappings to the proxy");
            }

            ScanCompleted?.Invoke(result);
        }

        return result;
    }

    /// <summary>
    /// Applies one source's model list to its mappings: re-enables returned models, disables models
    /// that stayed missing past the grace period, and registers models that are new. Caller holds
    /// <see cref="AppSettings.ModelMappingsSyncRoot"/>.
    /// </summary>
    private ReconcileOutcome ReconcileSource(
        DiscoverySource source,
        HashSet<string> present,
        List<ModelMapping> mappings,
        List<string> messages)
    {
        DateTime now = _utcNow();
        TimeSpan grace = TimeSpan.FromMinutes(Math.Max(1, _settings.ModelAutoDiscoveryOfflineMinutes));
        int reactivated = 0;
        int disabled = 0;
        bool changed = false;

        foreach (ModelMapping mapping in source.Members)
        {
            if (present.Contains(mapping.ModelName))
            {
                if (mapping.AutoMissingSinceUtc is not null)
                {
                    mapping.AutoMissingSinceUtc = null;
                    changed = true;
                }

                if (mapping.AutoDisabledUtc is not null)
                {
                    mapping.AutoDisabledUtc = null;
                    mapping.AutoDisabledReason = null;
                    mapping.IsEnabled = true;
                    reactivated++;
                    changed = true;
                    messages.Add($"{mapping.ProxyName}: re-enabled, the model is back upstream.");
                    Log.Information("Model auto-discovery re-enabled {Model}", mapping.ProxyName);
                }
                else if (mapping.AutoDisabledReason is not null)
                {
                    mapping.AutoDisabledReason = null;
                    changed = true;
                }

                continue;
            }

            if (mapping.AutoMissingSinceUtc is null)
            {
                mapping.AutoMissingSinceUtc = now;
                changed = true;
            }

            // Already off — either discovery got here first, or an operator switched it off. Either
            // way there is nothing to disable, and an operator's decision is never second-guessed.
            if (mapping.AutoDisabledUtc is not null || !mapping.IsEnabled)
                continue;

            if (now - mapping.AutoMissingSinceUtc.Value < grace)
                continue;

            mapping.IsEnabled = false;
            mapping.AutoDisabledUtc = now;
            mapping.AutoDisabledReason =
                $"Not present in the upstream model list since {mapping.AutoMissingSinceUtc.Value:u}.";
            disabled++;
            changed = true;
            messages.Add($"{mapping.ProxyName}: disabled, {mapping.AutoDisabledReason}");
            Log.Information("Model auto-discovery disabled {Model}: {Reason}",
                mapping.ProxyName, mapping.AutoDisabledReason);
        }

        int registered = 0;
        foreach (string modelId in present.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            // Proxy names are the routing key and the table's primary key, so a model id already in
            // use anywhere (by hand, or by another upstream) is left alone.
            bool taken = mappings.Any(m =>
                string.Equals(m.ProxyName, modelId, StringComparison.OrdinalIgnoreCase));
            if (taken)
                continue;

            ModelMapping created = source.Anchor.Clone();
            created.AssignNewId();
            created.ProxyName = modelId;
            created.ModelName = modelId;
            created.IsEnabled = true;
            created.AutoManaged = true;
            created.AutoMissingSinceUtc = null;
            created.AutoDisabledUtc = null;
            created.AutoDisabledReason = null;
            // Never inherit a compaction target: it would point every new model at the template's
            // summarizer, or at the template itself.
            created.ContextSummarizeModelId = null;
            created.ContextSummarizeModelName = null;

            mappings.Add(created);
            registered++;
            changed = true;
            messages.Add($"{created.ProxyName}: registered from {source.UpstreamUrl}.");
            Log.Information("Model auto-discovery registered {Model} from {Upstream}",
                created.ProxyName, source.UpstreamUrl);
        }

        return new ReconcileOutcome(registered, reactivated, disabled, changed);
    }

    /// <summary>
    /// Groups auto-managed mappings by upstream endpoint. The first auto-managed mapping of each
    /// group (by proxy name, for a stable choice) is the template new models are cloned from.
    /// </summary>
    private static List<DiscoverySource> BuildSources(List<ModelMapping> mappings)
    {
        List<DiscoverySource> sources = [];
        foreach (IGrouping<string, ModelMapping> group in mappings
            .Where(m => m.AutoManaged && !string.IsNullOrWhiteSpace(m.UpstreamUrl))
            .GroupBy(
                m => $"{m.UpstreamUrl.Trim()}\u001f{m.CredentialName}\u001f{(int)m.UpstreamType}",
                StringComparer.OrdinalIgnoreCase))
        {
            List<ModelMapping> members = [.. group.OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)];
            sources.Add(new DiscoverySource(members[0].UpstreamUrl.Trim(), members[0], members));
        }

        return sources;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _scanGate.Dispose();
    }

    private readonly record struct ReconcileOutcome(
        int Registered, int Reactivated, int Disabled, bool Changed);

    /// <summary>
    /// One upstream endpoint to poll, its auto-managed mappings (in stable proxy-name order), and the
    /// mapping new models are cloned from.
    /// </summary>
    private sealed record DiscoverySource(
        string UpstreamUrl,
        ModelMapping Anchor,
        IReadOnlyList<ModelMapping> Members);
}