using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Per-module assembly load context. Resolves module dependencies by probing the module's own
/// directory (where <c>CopyLocalLockFileAssemblies</c> places them beside the module DLL), and
/// is collectible so modules can be unloaded when disabled or removed. Shared assemblies
/// (contracts, Serilog, Microsoft.Data.Sqlite, ...) that already exist in the application
/// directory deliberately fall back to the default context so their types unify with the
/// host's copies.
/// </summary>
internal sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Cached resolver for the host application's deps.json. Used to determine whether the
    /// default (System) load context can resolve an assembly — the true indicator that
    /// deferring to the default context will succeed and types will unify with the host.
    /// </summary>
    private static AssemblyDependencyResolver? _hostResolver;
    private static readonly object _hostResolverLock = new();

    private static AssemblyDependencyResolver HostResolver
    {
        get
        {
            if (_hostResolver is null)
            {
                lock (_hostResolverLock)
                {
                    _hostResolver ??= new AssemblyDependencyResolver((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location);
                }
            }
            return _hostResolver;
        }
    }

    /// <summary>
    /// Live module contexts, registered when a context is created and removed when it is
    /// unloaded. Keyed by context (not directory) so two modules sharing a directory do not
    /// remove each other's registration, and so the default-context <c>Resolving</c> handler
    /// can resolve a dependency into its OWNING collectible context instead of Default.
    /// </summary>
    private static readonly List<ModuleAssemblyLoadContext> _liveContexts = [];
    private static readonly object _liveContextsLock = new();
    private static bool _resolvingHandlerRegistered;

    /// <summary>
    /// Cache of assemblies loaded in the default context, kept current via <c>AssemblyLoad</c>.
    /// Avoids enumerating <c>Default.Assemblies</c> on every resolution and enables the
    /// version comparison that rejects unifying with an older host copy.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Assembly> _defaultAssemblyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _defaultCacheLock = new();
    private static volatile bool _defaultCacheInitialized;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _moduleDirectory;
    private readonly string _moduleAssemblyFullPath;
    private readonly string _moduleAssemblyName;
    private readonly object _nativeCacheLock = new();
    private Dictionary<string, string>? _nativeDllCache;
    private Assembly? _moduleAssembly;

    public ModuleAssemblyLoadContext(string moduleAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(moduleAssemblyPath), isCollectible: true)
    {
        string fullPath = Path.GetFullPath(moduleAssemblyPath);
        _resolver = new AssemblyDependencyResolver(fullPath);
        _moduleDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        _moduleAssemblyFullPath = fullPath;
        _moduleAssemblyName = Path.GetFileNameWithoutExtension(fullPath);

        lock (_liveContextsLock)
        {
            _liveContexts.Add(this);

            // Registered under the same lock so two contexts constructed concurrently cannot
            // both pass the flag check and subscribe the default-context handler twice.
            if (!_resolvingHandlerRegistered)
            {
                _resolvingHandlerRegistered = true;
                AssemblyLoadContext.Default.Resolving += OnDefaultContextResolving;
            }
        }

        Unloading += ctx => { lock (_liveContextsLock) { _liveContexts.Remove(this); } };
    }

    /// <summary>
    /// Unloads this context and verifies on a background task that it actually gets collected.
    /// A surviving context means some reference (event handler, static, running task) still
    /// pins the module; that is logged so the leak is visible instead of silent.
    /// </summary>
    public void UnloadWithVerification()
    {
        string contextName = Name ?? "<unnamed>";
        WeakReference reference = new(this, trackResurrection: true);
        Unload();

        // The closure captures only locals — capturing 'this' would itself root the context.
        _ = Task.Run(() =>
        {
            for (int pass = 0; pass < 2 && reference.IsAlive; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (reference.IsAlive)
                Log.Warning("Module load context {Context} survived unload after GC; a lingering reference is keeping the module alive.", contextName);
        });
    }

    private static Assembly? GetDefaultAssembly(string name)
    {
        if (!_defaultCacheInitialized)
        {
            lock (_defaultCacheLock)
            {
                if (!_defaultCacheInitialized)
                {
                    // Subscribe before enumerating so an assembly loaded mid-scan is not missed;
                    // duplicate writes are harmless (the indexer overwrites). ALC has no load
                    // event of its own, so filter the AppDomain event to the default context.
                    AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
                    {
                        if (ReferenceEquals(AssemblyLoadContext.GetLoadContext(args.LoadedAssembly), AssemblyLoadContext.Default)
                            && args.LoadedAssembly.GetName().Name is { } loadedName)
                        {
                            _defaultAssemblyCache[loadedName] = args.LoadedAssembly;
                        }
                    };

                    foreach (Assembly assembly in AssemblyLoadContext.Default.Assemblies)
                    {
                        if (assembly.GetName().Name is { } loadedName)
                            _defaultAssemblyCache[loadedName] = assembly;
                    }

                    _defaultCacheInitialized = true;
                }
            }
        }

        return _defaultAssemblyCache.TryGetValue(name, out Assembly? cached) ? cached : null;
    }

    /// <summary>
    /// Attempts to load a dependency from the module's embedded manifest resources
    /// (small dependencies embedded at build time with a 'moduledep/' prefix).
    /// Returns null if the dependency is not embedded.
    /// </summary>
    private Assembly? TryLoadEmbeddedDependency(AssemblyName assemblyName)
    {
        // L22: resolve the module assembly once instead of enumerating Assemblies on every miss.
        Assembly? moduleAssembly = _moduleAssembly;
        if (moduleAssembly is null)
        {
            moduleAssembly = Assemblies.FirstOrDefault(
                a => !string.IsNullOrEmpty(a.Location) && string.Equals(a.Location, _moduleAssemblyFullPath, StringComparison.OrdinalIgnoreCase));
            if (moduleAssembly is not null)
                _moduleAssembly = moduleAssembly;
        }

        if (moduleAssembly is null)
            return null;

        string resourceName = $"moduledep/{assemblyName.Name}.dll";
        using Stream? stream = moduleAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        ms.Position = 0;
        return LoadFromStream(ms);
    }

    /// <summary>
    /// Fallback handler for the default load context. When the host's deps.json and base
    /// directory probing both fail, satisfy the request by loading a SEPARATE copy into the
    /// default context from a module that ships the dependency.
    /// <para>
    /// It is critical that this handler never returns an assembly belonging to a collectible
    /// module context: the runtime caches the result of <c>Default.Resolving</c> in Default's
    /// bind cache, so handing back a collectible instance (or loading into the owning module
    /// context) roots that context for the process lifetime and silently defeats module
    /// unload. A default-context copy is non-collectible and lives as long as the host needs
    /// it; the module keeps its own collectible copy with its own type identity.
    /// </para>
    /// A module's own assembly is never served here at all; only its dependencies. If no
    /// module ships the assembly, returning null leaves Default probing the host directory.
    /// </summary>
    private static Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        ModuleAssemblyLoadContext[] owners;
        lock (_liveContextsLock)
        {
            owners = [.. _liveContexts];
        }

        // A module's own assembly must never enter the default context.
        foreach (ModuleAssemblyLoadContext owner in owners)
        {
            if (string.Equals(owner._moduleAssemblyName, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        // Gather every module that ships this dependency. Serving the first owner blindly would
        // let two modules shipping different versions contaminate each other's bind; pick
        // deterministically by highest version and log the ambiguity.
        List<string> candidates = [];
        foreach (ModuleAssemblyLoadContext owner in owners)
        {
            string candidate = Path.Combine(owner._moduleDirectory, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return null;

        string chosen = candidates[0];
        if (candidates.Count > 1)
        {
            Version? best = null;
            foreach (string candidate in candidates)
            {
                try
                {
                    Version? version = AssemblyName.GetAssemblyName(candidate).Version;
                    if (version is not null && (best is null || version > best))
                    {
                        best = version;
                        chosen = candidate;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not read assembly version for {Path}", candidate);
                }
            }

            Log.Warning("Multiple modules ship {Assembly}; serving the highest-version copy to the default context from {Path}",
                assemblyName.Name, chosen);
        }

        try
        {
            // Load into Default (the requesting context), NOT a module owner. This avoids
            // pinning a collectible module context via Default's bind cache.
            return context.LoadFromAssemblyPath(chosen);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Default-context fallback load failed for {Assembly} from {Path}", assemblyName.Name, chosen);
            return null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        // 0. If the assembly is already loaded in the default context at the same or a higher
        //    version, reuse that exact instance so shared contract types (e.g. IKaeoModule)
        //    keep a single type identity. An OLDER host copy is a downgrade for this module:
        //    skip unification (and the host-resolver step) and load the module's own copy.
        bool rejectedDowngrade = false;
        try
        {
            if (GetDefaultAssembly(assemblyName.Name) is { } loaded)
            {
                Version? loadedVersion = loaded.GetName().Version;
                Version? requestedVersion = assemblyName.Version;
                if (requestedVersion is null || loadedVersion is null || loadedVersion >= requestedVersion)
                    return null;

                rejectedDowngrade = true;
                Log.Debug(
                    "Default context holds {Assembly} {LoadedVersion} but module {Context} requests {RequestedVersion}; loading the module's own copy",
                    assemblyName.Name, loadedVersion, Name, requestedVersion);
            }
        }
        catch (Exception ex)
        {
            // L21: log rather than silently swallow; fall through to the other resolution steps.
            Log.Debug(ex, "Default-context version unification check failed for {Assembly}", assemblyName.Name);
        }

        // 1. If the host's deps.json can resolve this assembly, defer to the default
        //    context so shared assemblies (contracts, Serilog, Microsoft.Data.Sqlite, ...)
        //    unify with the host's copies. Using the host's deps.json (not mere file
        //    existence) prevents module-specific dependencies that happen to also exist
        //    in the host directory from being incorrectly deferred.
        if (!rejectedDowngrade)
        {
            try
            {
                if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
                    return null;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Host AssemblyDependencyResolver failed for {Assembly}", assemblyName.Name);
            }
        }

        // 2. Module's own directory: probe a side-by-side file FIRST so an updated sibling DLL
        //    wins over a stale embedded copy (L22). Large deps (>= 10 MB) remain as separate files.
        string siblingCandidate = Path.Combine(_moduleDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(siblingCandidate))
            return LoadFromAssemblyPath(siblingCandidate);

        // 3. Embedded dependencies: small runtime dependencies (< 10 MB) embedded as manifest
        //    resources in the module DLL with a 'moduledep/' prefix.
        if (TryLoadEmbeddedDependency(assemblyName) is { } embedded)
            return embedded;

        try
        {
            string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null && File.Exists(resolved))
                return LoadFromAssemblyPath(resolved);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Module AssemblyDependencyResolver failed for {Assembly} in {Context}", assemblyName.Name, Name);
        }

        // 4. Not found in the module's own location: defer to the default context. The
        //    default context probes the host's base directory, so a shared assembly (contracts,
        //    Core, ...) sitting beside the host executable loads as a single instance there —
        //    keeping its type identity unified with the host. Loading it into this context
        //    instead would create a second, non-unified copy (e.g. a second IKaeoModule type
        //    that the host's typeof(IKaeoModule).IsAssignableFrom cannot see).
        Log.Debug("Assembly {Assembly} not found in module dir {ModuleDir}; deferring to default context",
            assemblyName.Name, _moduleDirectory);
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string fileName = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? unmanagedDllName
            : unmanagedDllName + ".dll";

        // 1. Application (EXE) directory first for shared native libraries.
        string hostCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(hostCandidate))
            return LoadUnmanagedDllFromPath(hostCandidate);

        // 2. Module's own directory: direct probe first, then a cached recursive scan
        //    (covers runtimes/{rid}/native/ and other NuGet native dependency layouts,
        //    preferring the current RID's native assets), then deps.json resolution.
        string siblingCandidate = Path.Combine(_moduleDirectory, fileName);
        if (File.Exists(siblingCandidate))
            return LoadUnmanagedDllFromPath(siblingCandidate);

        if (GetNativeDllCache().TryGetValue(fileName, out string? subdirCandidate))
            return LoadUnmanagedDllFromPath(subdirCandidate);

        try
        {
            string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (resolved is not null && File.Exists(resolved))
                return LoadUnmanagedDllFromPath(resolved);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AssemblyDependencyResolver failed for unmanaged {Dll} in {Context}", unmanagedDllName, Name);
        }

        // 3. Let the runtime fall back to its default probing (PATH, system directories).
        return IntPtr.Zero;
    }

    /// <summary>
    /// One recursive scan of the module directory, cached for the context's lifetime. Entries
    /// under <c>runtimes/&lt;current-rid&gt;/native</c> win over other platforms' copies of the
    /// same file name. A failed scan is not cached so a transient sharing violation retries.
    /// </summary>
    private Dictionary<string, string> GetNativeDllCache()
    {
        lock (_nativeCacheLock)
        {
            if (_nativeDllCache is not null)
                return _nativeDllCache;

            Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);
            bool scanCompleted = false;
            try
            {
                string ridNativeSegment = Path.Combine("runtimes", RuntimeInformation.RuntimeIdentifier, "native") + Path.DirectorySeparatorChar;
                // L23: skip reparse points so a junction/symlink in the module dir cannot drag a
                // huge external tree into the scan.
                EnumerationOptions scanOptions = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string path in Directory.EnumerateFiles(_moduleDirectory, "*.dll", scanOptions))
                {
                    string name = Path.GetFileName(path);
                    bool isCurrentRid = path.Contains(ridNativeSegment, StringComparison.OrdinalIgnoreCase);
                    if (isCurrentRid || !cache.ContainsKey(name))
                        cache[name] = path;
                }
                scanCompleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Native DLL directory scan failed in {ModuleDir}", _moduleDirectory);
            }

            if (scanCompleted)
                _nativeDllCache = cache;
            return cache;
        }
    }
}
