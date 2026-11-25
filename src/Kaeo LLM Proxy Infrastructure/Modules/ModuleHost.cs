using System.Reflection;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Loads and manages user-registered modules. Modules are registered explicitly through
/// browse-to-import and persisted in the <c>module_registry</c> table; the host never scans
/// directories or auto-imports assemblies. Each module runs in its own collectible
/// <see cref="ModuleAssemblyLoadContext"/> so it can be unloaded when disabled or removed.
/// </summary>
internal sealed class ModuleHost
{
    private readonly AppDatabase _database;
    private readonly AppSettings _settings;
    private readonly ModuleDatabaseGateway _databaseGateway;
    private readonly ModuleSecretProvider _secretProvider;
    private readonly ModulePackageLoader _packageLoader;
    private readonly LateBoundActivityLog _activityLog = new();
    private readonly List<LoadedModule> _loadedModules = [];

    // Guards every access to _loadedModules: enable/disable actions can fire from the tray menu
    // while the modules tab or an MCP session enumerates the list on another thread.
    private readonly object _modulesLock = new();

    // Per-module gates serialize enable/disable/remove so the check+load (or check+unload)
    // sequence is atomic per module without blocking unrelated modules.
    private readonly Dictionary<int, SemaphoreSlim> _moduleGates = [];
    private readonly object _moduleGatesLock = new();

    private SemaphoreSlim GetModuleGate(int id)
    {
        lock (_moduleGatesLock)
        {
            if (!_moduleGates.TryGetValue(id, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _moduleGates[id] = gate;
            }
            return gate;
        }
    }

    /// <summary>
    /// Runs a module lifecycle call with a 30s bound. On timeout the call is abandoned with a
    /// warning and a fault-observing continuation so the zombie task cannot go unobserved;
    /// returns false. Other exceptions propagate to the caller's existing handling.
    /// </summary>
    private static async Task<bool> TryRunLifecycleAsync(string moduleName, string operation, Func<Task> action)
    {
        Task task = Task.Run(action);
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Log.Warning("Module {Name} did not complete {Operation} within 30s; abandoning the call.", moduleName, operation);
            _ = task.ContinueWith(
                t => Log.Warning(t.Exception, "Abandoned module {Name} {Operation} call later faulted", moduleName, operation),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return false;
        }
    }

    /// <summary>
    /// Constructs the module instance inside the same 30s bound as other lifecycle calls so a
    /// hanging parameterless constructor (or static initializer) cannot block import/startup
    /// indefinitely. On timeout the call is abandoned and a TimeoutException propagates.
    /// </summary>
    private static async Task<IKaeoModule> CreateModuleBoundedAsync(string name, Assembly assembly)
    {
        IKaeoModule? created = null;
        bool completed = await TryRunLifecycleAsync(name, "construction", () => { created = CreateModuleInstance(assembly); return Task.CompletedTask; }).ConfigureAwait(false);
        if (!completed)
            throw new TimeoutException($"Module '{name}' construction did not complete within 30s.");
        return created!;
    }

    public ModuleHost(AppDatabase database, AppSettings settings)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _databaseGateway = new ModuleDatabaseGateway(database);
        _secretProvider = new ModuleSecretProvider(settings);
        string dataDirectory = Path.GetDirectoryName(database.DatabasePath) ?? AppContext.BaseDirectory;
        _packageLoader = new ModulePackageLoader(settings, dataDirectory);
    }

    /// <summary>
    /// Binds the real MCP activity log sink once the host's MCP log store exists. Modules are
    /// initialized before that store is created, so they receive a forwarding sink whose target
    /// is set here.
    /// </summary>
    public void BindActivityLog(IMcpActivityLog target) => _activityLog.SetTarget(target);

    /// <summary>
    /// Snapshot of the modules currently loaded and initialized. Returns a copy so callers can
    /// enumerate safely while another thread enables, disables, or removes modules.
    /// </summary>
    public IReadOnlyList<LoadedModule> LoadedModules
    {
        get
        {
            lock (_modulesLock)
            {
                return [.. _loadedModules];
            }
        }
    }

    /// <summary>Raised after modules are loaded, imported, removed, enabled, or disabled.</summary>
    public event EventHandler? ModulesChanged;

    /// <summary>Returns all registered modules (enabled or not) for display in the UI.</summary>
    public IReadOnlyList<ModuleRegistryEntry> GetRegistryEntries() => _database.LoadModuleRegistry();

    /// <summary>
    /// Collects the MCP tool target instances contributed by loaded modules implementing
    /// <see cref="IMcpToolModule"/> for the session described by <paramref name="session"/>.
    /// A module that fails to produce targets is logged and skipped so one bad module never
    /// breaks the MCP server.
    /// </summary>
    public IReadOnlyList<object> GetMcpToolTargets(McpSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<object> targets = [];

        // Snapshot before enumerating: module code may call back into the host (or another thread
        // may enable/disable modules) while targets are being created.
        LoadedModule[] snapshot;
        lock (_modulesLock)
        {
            snapshot = [.. _loadedModules];
        }

        foreach (LoadedModule loaded in snapshot)
        {
            if (loaded.Module is not IMcpToolModule toolModule)
                continue;

            try
            {
                // Bound each module's target creation: a hanging module (deadlock / blocking I/O)
                // must not wedge the initialize POST indefinitely. Run off the request thread and
                // skip the module if it exceeds the per-session budget. The factory contract is
                // synchronous (McpServer.Create), so the wait is necessarily blocking here, but it
                // runs on a thread-pool thread and is now time-bounded rather than unbounded.
                Task<IReadOnlyList<object>> task = Task.Run(() => toolModule.CreateMcpToolTargets(session));
                IReadOnlyList<object> produced;
                try
                {
                    produced = task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    Log.Warning("Module {Name} did not create MCP tool targets within 10s; skipping it for this session.", loaded.Entry.Name);
                    _ = task.ContinueWith(
                        t => Log.Warning(t.Exception, "Abandoned module {Name} tool-target creation later faulted", loaded.Entry.Name),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    continue;
                }

                targets.AddRange(produced);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Module {Name} failed to create MCP tool targets", loaded.Entry.Name);
            }
        }

        return targets;
    }

    /// <summary>
    /// Loads all enabled modules from the registry. A module that fails to load records its
    /// error on the registry entry and never blocks host startup or other modules.
    /// </summary>
    public void LoadRegisteredModules()
    {
        IReadOnlyList<ModuleRegistryEntry> entries;
        try
        {
            entries = _database.LoadModuleRegistry();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // A sharing violation from a concurrent instance must not crash host startup;
            // modules simply load on the next start once the database is readable.
            Log.Warning(ex, "Could not read the module registry; no modules loaded this start");
            return;
        }

        foreach (ModuleRegistryEntry entry in entries)
        {
            if (!entry.IsEnabled)
                continue;

            try
            {
                // Startup is a genuinely synchronous caller: modules must be loaded before the
                // WinForms message loop starts on the STA thread, so blocking here is intentional
                // and STA-safe (making Main async would move Application.Run off the STA thread).
                LoadedModule loaded = LoadModuleAsync(entry).GetAwaiter().GetResult();
                lock (_modulesLock)
                {
                    _loadedModules.Add(loaded);
                }

                TryUpdateEntry(entry);
                Log.Information(
                    "Loaded module {Name} {Version} from {Path}",
                    entry.Name, entry.Version, entry.AssemblyPath);
            }
            catch (Exception ex)
            {
                entry.LastError = ex.Message;
                TryUpdateEntry(entry);
                Log.Warning(ex, "Failed to load module from {Path}", entry.AssemblyPath);
            }
        }

        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Validates and imports a module assembly from <paramref name="assemblyPath"/>: the file
    /// must contain exactly one <see cref="IKaeoModule"/> implementation with a unique module
    /// id. On success the module is initialized (schema applied + settings loaded), registered
    /// in the database, and kept loaded. Throws with a user-presentable message on failure.
    /// </summary>
    public async Task<ModuleRegistryEntry> ImportAsync(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);

        try
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"The module file '{fullPath}' does not exist.", fullPath);

            // Every import passes through the packaging/policy seam: .kaeomod packages are
            // verified and extracted first; loose DLLs get the unsigned-policy check.
            string importPath = fullPath;
            string signatureState = ModuleSignatureStates.Unsigned;
            string? signerKeyId = null;
            if (ModulePackageLoader.IsPackagePath(fullPath))
            {
                ModulePackageLoader.PreparedPackage prepared = _packageLoader.PreparePackage(fullPath);
                importPath = prepared.EntryAssemblyPath;
                signatureState = prepared.SignatureState;
                signerKeyId = prepared.SignerKeyId;
            }
            else
            {
                _packageLoader.CheckLooseAssembly(fullPath);
            }

            if (_database.FindModuleRegistryByPath(importPath) is not null)
                throw new InvalidOperationException($"This module is already registered:\n{importPath}");

            ModuleAssemblyLoadContext loadContext = new(importPath);
            IKaeoModule module;
            try
            {
                Assembly assembly = loadContext.LoadFromAssemblyPath(importPath);
                module = await CreateModuleBoundedAsync(importPath, assembly).ConfigureAwait(false);
            }
            catch
            {
                loadContext.UnloadWithVerification();
                throw;
            }

            // Two assemblies must not fight over the same settings keys and schema objects.
            if (_database.LoadModuleRegistry().Any(e =>
                    string.Equals(e.ModuleId, module.Id, StringComparison.OrdinalIgnoreCase)))
            {
                loadContext.UnloadWithVerification();
                throw new InvalidOperationException($"A module with id '{module.Id}' is already registered.");
            }

            ModuleRegistryEntry entry = new()
            {
                AssemblyPath = importPath,
                ModuleId = module.Id,
                Name = module.Name,
                Version = module.Version,
                IsEnabled = true,
                RegisteredUtc = DateTime.UtcNow,
                SignatureState = signatureState,
                SignerKeyId = signerKeyId,
            };

            try
            {
                string importDataDirectory = Path.GetDirectoryName(_database.DatabasePath) ?? AppContext.BaseDirectory;
                ModuleContext context = new(_databaseGateway, _secretProvider, BuildHostInfo(), _activityLog, importDataDirectory);
                bool initialized = await TryRunLifecycleAsync(module.Name, "Initialize", () => { module.Initialize(context); return Task.CompletedTask; }).ConfigureAwait(false);
                if (!initialized)
                    throw new TimeoutException($"Module '{module.Name}' Initialize did not complete within 30s.");
            }
            catch
            {
                loadContext.UnloadWithVerification();
                throw;
            }

            try
            {
                _database.InsertModuleRegistry(entry);
                lock (_modulesLock)
                {
                    _loadedModules.Add(new LoadedModule { Entry = entry, Module = module, LoadContext = loadContext });
                }
            }
            catch
            {
                // Roll back the initialized module so a failed registration does not leave a
                // running module or a pinned load context behind.
                if (module is IRunnableModule { IsRunning: true } runnable)
                {
                    try
                    {
                        await TryRunLifecycleAsync(module.Name, "StopAsync", runnable.StopAsync).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error stopping module {Name} during import rollback", module.Name);
                    }
                }

                loadContext.UnloadWithVerification();
                throw;
            }

            Log.Information("Imported module {Name} {Version} from {Path} ({SignatureState})", module.Name, module.Version, importPath, signatureState);
            ModulesChanged?.Invoke(this, EventArgs.Empty);
            return entry;
        }
        catch (Exception ex)
        {
            // Surface the full failure (message + stack) to the file log AND to the MCP activity
            // log (database + Logs tab, MCP sub-tab) so a failed import is visible in the UI
            // without needing to read the file log.
            Log.Warning(ex, "Failed to import module {Path}", fullPath);
            _activityLog.Write(new McpActivityEntry("Module", "import")
            {
                Target = Path.GetFileName(fullPath),
                IsError = true,
                ErrorMessage = ex.ToString(),
                RequestDetail = $"Import module: {fullPath}",
            });
            throw;
        }
    }

    /// <summary>
    /// Enables or disables a registered module. Enabling loads and initializes the module;
    /// disabling stops its service (when runnable) and unloads the assembly.
    /// </summary>
    public async Task SetEnabledAsync(ModuleRegistryEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SemaphoreSlim gate = GetModuleGate(entry.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled)
            {
                bool alreadyLoaded;
                lock (_modulesLock)
                {
                    alreadyLoaded = _loadedModules.Any(m => m.Entry.Id == entry.Id);
                }

                if (!alreadyLoaded)
                {
                    LoadedModule loaded = await LoadModuleAsync(entry).ConfigureAwait(false);
                    lock (_modulesLock)
                    {
                        _loadedModules.Add(loaded);
                    }
                }
            }
            else
            {
                LoadedModule? loaded;
                lock (_modulesLock)
                {
                    loaded = _loadedModules.FirstOrDefault(m => m.Entry.Id == entry.Id);
                }

                if (loaded is not null)
                    await UnloadAsync(loaded).ConfigureAwait(false);
            }

            // Persist and publish only after the load/unload succeeded; on failure the entry
            // keeps its previous state and the exception propagates to the caller.
            entry.IsEnabled = enabled;
            TryUpdateEntry(entry);
            ModulesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes a module from the registry, stopping and unloading it first when loaded.</summary>
    public async Task RemoveAsync(ModuleRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SemaphoreSlim gate = GetModuleGate(entry.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LoadedModule? loaded;
            lock (_modulesLock)
            {
                loaded = _loadedModules.FirstOrDefault(m => m.Entry.Id == entry.Id);
            }

            if (loaded is not null)
                await UnloadAsync(loaded).ConfigureAwait(false);

            try
            {
                _database.DeleteModuleRegistry(entry.Id);
                Log.Information("Removed module {Name} ({Path}) from the registry", entry.Name, entry.AssemblyPath);
            }
            catch (Exception ex) when (ex is IOException or SqliteException)
            {
                // The module is already unloaded; a sharing violation only delays the registry
                // row's removal until the next successful write rather than crashing management.
                Log.Warning(ex, "Could not delete module {Name} from the registry; it was unloaded and the row persists until the next successful write", entry.Name);
            }

            ModulesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            gate.Release();

            // L18: drop the per-module gate so removed modules don't accumulate semaphores for
            // the process lifetime. A later operation on the same id creates a fresh gate. The
            // semaphore holds no unmanaged handle (AvailableWaitHandle is never touched), so
            // removing the reference is enough for the GC to reclaim it without risking a
            // concurrent waiter's ObjectDisposedException.
            lock (_moduleGatesLock)
            {
                _moduleGates.Remove(entry.Id);
            }
        }
    }

    /// <summary>Stops all runnable modules (used during application shutdown).</summary>
    public async Task StopAllAsync()
    {
        // Snapshot first: StopAsync runs module code that may re-enter the host.
        LoadedModule[] snapshot;
        lock (_modulesLock)
        {
            snapshot = [.. _loadedModules];
        }

        foreach (LoadedModule loaded in snapshot)
        {
            if (loaded.Module is not IRunnableModule { IsRunning: true } runnable)
                continue;

            try
            {
                await TryRunLifecycleAsync(loaded.Entry.Name ?? loaded.Entry.AssemblyPath, "StopAsync", runnable.StopAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error stopping module {Name} during shutdown", loaded.Entry.Name);
            }
        }
    }

    /// <summary>Builds the host endpoint snapshot handed to modules during initialization.</summary>
    public HostInfo BuildHostInfo()
    {
        // L20: normalize empty/whitespace/null to "localhost" so the HostInfo ctor's
        // ThrowIfNullOrWhiteSpace never breaks service construction or module loads.
        string host = (_settings.ListenAddress ?? "localhost").Trim();
        if (host.Length == 0)
            host = "localhost";
        string displayHost = host is "0.0.0.0" or "+" or "[::]" or "::"
            ? "localhost"
            : host;

        string? specUrl = _settings.EnableApiExplorer
            ? $"http://{displayHost}:{_settings.ListenPort}/openapi/v1/openapi.json"
            : null;

        return new HostInfo(host, _settings.ListenPort, _settings.EnableApiExplorer, specUrl);
    }

    private async Task<LoadedModule> LoadModuleAsync(ModuleRegistryEntry entry)
    {
        if (!File.Exists(entry.AssemblyPath))
            throw new FileNotFoundException(
                $"The registered module file no longer exists:\n{entry.AssemblyPath}", entry.AssemblyPath);

        // Re-verify installed packages (payload hashes + signature) and apply the load policy
        // before the assembly is touched, so post-install tampering or a policy flip blocks load.
        (entry.SignatureState, entry.SignerKeyId) = _packageLoader.VerifyRegisteredAssembly(entry.AssemblyPath);

        ModuleAssemblyLoadContext loadContext = new(entry.AssemblyPath);
        IKaeoModule module;
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(entry.AssemblyPath);
            module = await CreateModuleBoundedAsync(entry.Name ?? entry.AssemblyPath, assembly).ConfigureAwait(false);
            // Derive the application's data directory from the configured DB path and pass it
            // to the module so it stores its files beside the host database.
            string? appDbPath = _database.DatabasePath;
            string dataDirectory = Path.GetDirectoryName(appDbPath) ?? AppContext.BaseDirectory;
            ModuleContext context = new(_databaseGateway, _secretProvider, BuildHostInfo(), _activityLog, dataDirectory);
            bool initialized = await TryRunLifecycleAsync(entry.Name ?? entry.AssemblyPath, "Initialize", () => { module.Initialize(context); return Task.CompletedTask; }).ConfigureAwait(false);
            if (!initialized)
                throw new TimeoutException($"Module '{entry.Name ?? entry.AssemblyPath}' Initialize did not complete within 30s.");
        }
        catch
        {
            loadContext.UnloadWithVerification();
            throw;
        }

        // Refresh the metadata the module reports about itself and clear stale errors.
        entry.ModuleId = module.Id;
        entry.Name = module.Name;
        entry.Version = module.Version;
        entry.LastError = null;

        return new LoadedModule { Entry = entry, Module = module, LoadContext = loadContext };
    }

    private async Task UnloadAsync(LoadedModule loaded)
    {
        try
        {
            if (loaded.Module is IRunnableModule { IsRunning: true } runnable)
                await TryRunLifecycleAsync(loaded.Entry.Name ?? loaded.Entry.AssemblyPath, "StopAsync", runnable.StopAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping module {Name} during unload", loaded.Entry.Name);
        }

        // Give the module a chance to release resources (HttpClients, timers, CTSs, file handles,
        // background loops) before its assembly is unloaded. IKaeoModule does not require disposal,
        // so honor it opportunistically when the module implements it; a lingering handler or
        // undisposed resource is the most common cause of a module surviving unload.
        try
        {
            if (loaded.Module is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (loaded.Module is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing module {Name} during unload", loaded.Entry.Name);
        }

        lock (_modulesLock)
        {
            _loadedModules.Remove(loaded);
        }

        loaded.LoadContext.UnloadWithVerification();
    }

    private static IKaeoModule CreateModuleInstance(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Exception? first = ex.LoaderExceptions.FirstOrDefault(e => e is not null);
            throw new InvalidOperationException(
                $"The module assembly could not be fully loaded: {first?.Message ?? ex.Message}", ex);
        }

        List<Type> candidates = [.. types
            .Where(t => typeof(IKaeoModule).IsAssignableFrom(t)
                && t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })];

        if (candidates.Count == 0)
            throw new InvalidOperationException(BuildNoModuleDiagnostic(assembly, types));

        if (candidates.Count > 1)
            throw new InvalidOperationException(
                "The assembly contains more than one IKaeoModule implementation. Only one module per assembly is supported.");

        Type moduleType = candidates[0];
        try
        {
            return (IKaeoModule)(Activator.CreateInstance(moduleType)
                ?? throw new InvalidOperationException($"Failed to create an instance of '{moduleType.FullName}'."));
        }
        catch (MissingMethodException ex)
        {
            throw new InvalidOperationException(
                $"The module '{moduleType.FullName}' could not be constructed (a parameterless constructor is required): {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Builds a detailed diagnostic explaining why no <see cref="IKaeoModule"/> implementation was
    /// found. Distinguishes the two failure modes: the assembly has no module-like types at all
    /// (wrong file selected), versus the assembly's IKaeoModule resolving to a different assembly
    /// instance than the host's (type identity mismatch from a stale or mis-built module).
    /// </summary>
    private static string BuildNoModuleDiagnostic(Assembly assembly, Type[] types)
    {
        List<string> lines =
        [
            "The assembly is not a Kaeo LLM Proxy module: no class implements IKaeoModule.",
            "",
            $"  Module assembly : {assembly.FullName}",
            $"  Module location : {assembly.Location}",
            $"  Host IKaeoModule: {typeof(IKaeoModule).Assembly.FullName} @ {typeof(IKaeoModule).Assembly.Location}",
            "",
        ];

        IEnumerable<Type> ikInterfaces = types
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.Name == "IKaeoModule")
            .Distinct();

        if (ikInterfaces.Any())
        {
            lines.Add("IKaeoModule interfaces found on module types (identity check):");
            foreach (Type i in ikInterfaces)
            {
                bool same = ReferenceEquals(i, typeof(IKaeoModule));
                lines.Add($"  {i.FullName} [{i.Assembly.GetName().Name}, v{i.Assembly.GetName().Version}] @ {i.Assembly.Location}");
                lines.Add($"    same type identity as host's IKaeoModule? {same}");
                if (!same)
                {
                    lines.Add("    ^^ TYPE IDENTITY MISMATCH: the module's IKaeoModule came from a different");
                    lines.Add("       'Kaeo LLM Proxy Core' assembly instance than the host's. This happens when");
                    lines.Add("       a stale/mis-built module bundles its own Core, or the host is an older");
                    lines.Add("       build whose load context does not unify shared assemblies.");
                }
            }
        }
        else
        {
            lines.Add("No IKaeoModule interface found on any type in the assembly.");
            lines.Add("This usually means the selected file is not a Kaeo LLM Proxy module at all");
            lines.Add("(e.g. a stale build, a combined 'Kaeo LLM Proxy Modules.dll', or a dependency).");
            lines.Add("");
            lines.Add("Public non-nested classes in the assembly:");
            foreach (Type t in types.Where(t => t.IsClass && !t.IsNested && !t.IsAbstract))
                lines.Add($"  {t.FullName}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void TryUpdateEntry(ModuleRegistryEntry entry)
    {
        try
        {
            _database.UpdateModuleRegistry(entry);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // A sharing violation from a concurrent instance must not crash module management;
            // the registry state simply persists on the next successful write.
            Log.Warning(ex, "Could not persist module registry state for {Path}", entry.AssemblyPath);
        }
    }
}
