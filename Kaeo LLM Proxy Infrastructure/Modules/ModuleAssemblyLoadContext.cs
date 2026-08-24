using System.Reflection;
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
                    _hostResolver ??= new AssemblyDependencyResolver(Assembly.GetEntryAssembly()!.Location);
                }
            }
            return _hostResolver;
        }
    }

    /// <summary>
    /// Live module directories, registered when a context is created and removed when it is
    /// unloaded. Used by the default-context <c>Resolving</c> handler to locate module
    /// dependencies that the host's deps.json does not list.
    /// </summary>
    private static readonly HashSet<string> _moduleDirectories = new();
    private static readonly object _moduleDirectoriesLock = new();
    private static bool _resolvingHandlerRegistered;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _moduleDirectory;
    private readonly string _moduleAssemblyFullPath;

    public ModuleAssemblyLoadContext(string moduleAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(moduleAssemblyPath), isCollectible: true)
    {
        string fullPath = Path.GetFullPath(moduleAssemblyPath);
        _resolver = new AssemblyDependencyResolver(fullPath);
        _moduleDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        _moduleAssemblyFullPath = fullPath;

        lock (_moduleDirectoriesLock)
        {
            _moduleDirectories.Add(_moduleDirectory);
        }

        if (!_resolvingHandlerRegistered)
        {
            _resolvingHandlerRegistered = true;
            AssemblyLoadContext.Default.Resolving += OnDefaultContextResolving;
        }

        Unloading += ctx => { lock (_moduleDirectoriesLock) { _moduleDirectories.Remove(_moduleDirectory); } };
    }

    /// <summary>
    /// Attempts to load a dependency from the module's embedded manifest resources
    /// (small dependencies embedded at build time with a 'moduledep/' prefix).
    /// Returns null if the dependency is not embedded.
    /// </summary>
    private Assembly? TryLoadEmbeddedDependency(AssemblyName assemblyName)
    {
        Assembly? moduleAssembly = Assemblies.FirstOrDefault(
            a => !string.IsNullOrEmpty(a.Location) && string.Equals(a.Location, _moduleAssemblyFullPath, StringComparison.OrdinalIgnoreCase));
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
    /// directory probing both fail, check each loaded module's directory for the requested
    /// assembly. This covers cases where the default context encounters a module-specific
    /// type reference (e.g. through exception type matching or cross-context casts).
    /// </summary>
    private static Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        string[] dirs;
        lock (_moduleDirectoriesLock)
        {
            dirs = _moduleDirectories.ToArray();
        }

        foreach (string dir in dirs)
        {
            string candidate = Path.Combine(dir, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
                return context.LoadFromAssemblyPath(candidate);
        }
        return null;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        // 0. If the assembly is already loaded in the default context, reuse that exact
        //    instance. This is the strongest guarantee that shared contract types (e.g.
        //    IKaeoModule) keep a single type identity between the host and this module.
        try
        {
            foreach (Assembly a in AssemblyLoadContext.Default.Assemblies)
            {
                if (string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
        }
        catch
        {
            // Ignore enumeration failures; fall through to the other resolution steps.
        }

        // 1. If the host's deps.json can resolve this assembly, defer to the default
        //    context so shared assemblies (contracts, Serilog, Microsoft.Data.Sqlite, ...)
        //    unify with the host's copies. Using the host's deps.json (not mere file
        //    existence) prevents module-specific dependencies that happen to also exist
        //    in the host directory from being incorrectly deferred.
        try
        {
            if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
                return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Host AssemblyDependencyResolver failed for {Assembly}", assemblyName.Name);
        }

        // 2. Embedded dependencies: small runtime dependencies (< 10 MB) are embedded
        //    as manifest resources in the module DLL with a 'moduledep/' prefix.
        if (TryLoadEmbeddedDependency(assemblyName) is { } embedded)
            return embedded;

        // 3. Module's own directory: direct sibling probe first (for large deps >= 10 MB
        //    that remain as separate files), then deps.json resolution as a fallback.
        string siblingCandidate = Path.Combine(_moduleDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(siblingCandidate))
            return LoadFromAssemblyPath(siblingCandidate);

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

        // 2. Module's own directory: direct probe first, then recursive subdirectory
        //    search (covers runtimes/{rid}/native/ and other NuGet native dependency
        //    layouts), then deps.json resolution as a fallback.
        string siblingCandidate = Path.Combine(_moduleDirectory, fileName);
        if (File.Exists(siblingCandidate))
            return LoadUnmanagedDllFromPath(siblingCandidate);

        try
        {
            string? subdirCandidate = Directory
                .EnumerateFiles(_moduleDirectory, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (subdirCandidate is not null)
                return LoadUnmanagedDllFromPath(subdirCandidate);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Recursive native DLL probe failed in {ModuleDir}", _moduleDirectory);
        }

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
}
