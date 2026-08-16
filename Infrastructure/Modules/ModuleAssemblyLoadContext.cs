using System.Reflection;
using System.Runtime.Loader;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Per-module assembly load context. Resolves module dependencies via the module's deps.json
/// and probes the module's own directory, and is collectible so modules can be unloaded when
/// disabled or removed. Shared assemblies (contracts, Serilog, Microsoft.Data.Sqlite, ...) that
/// already exist in the application directory deliberately fall back to the default context so
/// their types unify with the host's copies.
/// </summary>
internal sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _moduleDirectory;

    public ModuleAssemblyLoadContext(string moduleAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(moduleAssemblyPath), isCollectible: true)
    {
        string fullPath = Path.GetFullPath(moduleAssemblyPath);
        _resolver = new AssemblyDependencyResolver(fullPath);
        _moduleDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        // Assemblies that ship with the application must resolve to the host's already-loaded
        // copies so interface and logging types unify across the module boundary. Returning
        // null defers resolution to the default context.
        string hostCandidate = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(hostCandidate))
            return null;

        string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
            return LoadFromAssemblyPath(resolved);

        // Probe the module's directory for sibling dependencies not covered by deps.json.
        string siblingCandidate = Path.Combine(_moduleDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(siblingCandidate))
            return LoadFromAssemblyPath(siblingCandidate);

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolved is not null ? LoadUnmanagedDllFromPath(resolved) : IntPtr.Zero;
    }
}
