namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Contract every Kaeo LLM Proxy module must implement. The host discovers implementations of
/// this interface inside registered module assemblies and drives their lifecycle:
/// construct (parameterless constructor) → <see cref="Initialize"/>. Modules that render a host
/// UI additionally implement <see cref="IModuleUi"/>.
/// Modules must reference only this contracts assembly, never the host application, so new
/// modules can be added without any host code changes.
/// </summary>
/// <remarks>
/// A module that owns disposable resources (HttpClients, timers, cancellation token sources,
/// file handles, background loops) should also implement <see cref="IDisposable"/> or
/// <see cref="IAsyncDisposable"/>. The host disposes the module instance opportunistically when
/// it is disabled or removed, before unloading its assembly; releasing resources there is what
/// allows the collectible load context to actually be reclaimed.
/// </remarks>
public interface IKaeoModule
{
    /// <summary>Stable unique identifier for this module, e.g. <c>kaeo.mcp</c>.</summary>
    string Id { get; }

    /// <summary>Human-friendly display name, e.g. <c>MCP Server</c>.</summary>
    string Name { get; }

    /// <summary>Module version shown in the Modules tab.</summary>
    string Version { get; }

    /// <summary>Short description of what this module provides.</summary>
    string Description { get; }

    /// <summary>
    /// Called once after construction with the services the host makes available to modules.
    /// Modules should apply their database schema and load persisted settings here. Must not
    /// start any network services — see <see cref="IRunnableModule"/> for that.
    /// </summary>
    void Initialize(ModuleContext context);
}
