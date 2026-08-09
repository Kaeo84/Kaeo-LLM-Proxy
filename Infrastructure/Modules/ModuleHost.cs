using System.Reflection;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Modules;
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
    private readonly List<LoadedModule> _loadedModules = [];

    public ModuleHost(AppDatabase database, AppSettings settings)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _databaseGateway = new ModuleDatabaseGateway(database);
        _secretProvider = new ModuleSecretProvider(settings);
    }

    /// <summary>Modules currently loaded and initialized.</summary>
    public IReadOnlyList<LoadedModule> LoadedModules => _loadedModules;

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

        foreach (LoadedModule loaded in _loadedModules)
        {
            if (loaded.Module is not IMcpToolModule toolModule)
                continue;

            try
            {
                targets.AddRange(toolModule.CreateMcpToolTargets(session));
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
        foreach (ModuleRegistryEntry entry in _database.LoadModuleRegistry())
        {
            if (!entry.IsEnabled)
                continue;

            try
            {
                LoadedModule loaded = LoadModule(entry);
                _loadedModules.Add(loaded);
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
    public ModuleRegistryEntry Import(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The module file '{fullPath}' does not exist.", fullPath);

        if (_database.FindModuleRegistryByPath(fullPath) is not null)
            throw new InvalidOperationException($"This module is already registered:\n{fullPath}");

        ModuleAssemblyLoadContext loadContext = new(fullPath);
        IKaeoModule module;
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(fullPath);
            module = CreateModuleInstance(assembly);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        // Two assemblies must not fight over the same settings keys and schema objects.
        if (_database.LoadModuleRegistry().Any(e =>
                string.Equals(e.ModuleId, module.Id, StringComparison.OrdinalIgnoreCase)))
        {
            loadContext.Unload();
            throw new InvalidOperationException($"A module with id '{module.Id}' is already registered.");
        }

        ModuleRegistryEntry entry = new()
        {
            AssemblyPath = fullPath,
            ModuleId = module.Id,
            Name = module.Name,
            Version = module.Version,
            IsEnabled = true,
            RegisteredUtc = DateTime.UtcNow,
        };

        try
        {
            module.Initialize(new ModuleContext(_databaseGateway, _secretProvider, BuildHostInfo()));
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        _database.InsertModuleRegistry(entry);
        _loadedModules.Add(new LoadedModule { Entry = entry, Module = module, LoadContext = loadContext });

        Log.Information("Imported module {Name} {Version} from {Path}", module.Name, module.Version, fullPath);
        ModulesChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>
    /// Enables or disables a registered module. Enabling loads and initializes the module;
    /// disabling stops its service (when runnable) and unloads the assembly.
    /// </summary>
    public async Task SetEnabledAsync(ModuleRegistryEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.IsEnabled = enabled;

        if (enabled)
        {
            if (!_loadedModules.Any(m => m.Entry.Id == entry.Id))
            {
                LoadedModule loaded = LoadModule(entry);
                _loadedModules.Add(loaded);
            }
        }
        else
        {
            LoadedModule? loaded = _loadedModules.FirstOrDefault(m => m.Entry.Id == entry.Id);
            if (loaded is not null)
                await UnloadAsync(loaded).ConfigureAwait(false);
        }

        TryUpdateEntry(entry);
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a module from the registry, stopping and unloading it first when loaded.</summary>
    public async Task RemoveAsync(ModuleRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        LoadedModule? loaded = _loadedModules.FirstOrDefault(m => m.Entry.Id == entry.Id);
        if (loaded is not null)
            await UnloadAsync(loaded).ConfigureAwait(false);

        _database.DeleteModuleRegistry(entry.Id);
        Log.Information("Removed module {Name} ({Path}) from the registry", entry.Name, entry.AssemblyPath);
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops all runnable modules (used during application shutdown).</summary>
    public async Task StopAllAsync()
    {
        foreach (LoadedModule loaded in _loadedModules)
        {
            if (loaded.Module is not IRunnableModule { IsRunning: true } runnable)
                continue;

            try
            {
                await runnable.StopAsync().ConfigureAwait(false);
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
        string host = _settings.ListenAddress.Trim();
        string displayHost = host.Length == 0 || host is "0.0.0.0" or "+" or "[::]" or "::"
            ? "localhost"
            : host;

        string? specUrl = _settings.EnableApiExplorer
            ? $"http://{displayHost}:{_settings.ListenPort}/swagger/v1/swagger.json"
            : null;

        return new HostInfo(_settings.ListenAddress, _settings.ListenPort, _settings.EnableApiExplorer, specUrl);
    }

    private LoadedModule LoadModule(ModuleRegistryEntry entry)
    {
        if (!File.Exists(entry.AssemblyPath))
            throw new FileNotFoundException(
                $"The registered module file no longer exists:\n{entry.AssemblyPath}", entry.AssemblyPath);

        ModuleAssemblyLoadContext loadContext = new(entry.AssemblyPath);
        IKaeoModule module;
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(entry.AssemblyPath);
            module = CreateModuleInstance(assembly);
            module.Initialize(new ModuleContext(_databaseGateway, _secretProvider, BuildHostInfo()));
        }
        catch
        {
            loadContext.Unload();
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
                await runnable.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping module {Name} during unload", loaded.Entry.Name);
        }

        _loadedModules.Remove(loaded);
        loaded.LoadContext.Unload();
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
            throw new InvalidOperationException(
                "The assembly is not a Kaeo LLM Proxy module: no class implements IKaeoModule.");

        if (candidates.Count > 1)
            throw new InvalidOperationException(
                "The assembly contains more than one IKaeoModule implementation. Only one module per assembly is supported.");

        Type moduleType = candidates[0];
        try
        {
            return (IKaeoModule)(Activator.CreateInstance(moduleType)
                ?? throw new InvalidOperationException($"Failed to create an instance of '{moduleType.FullName}'."));
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The module '{moduleType.FullName}' could not be constructed (a parameterless constructor is required): {ex.Message}",
                ex);
        }
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
