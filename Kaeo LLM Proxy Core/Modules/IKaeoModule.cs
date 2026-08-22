namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Contract every Kaeo LLM Proxy module must implement. The host discovers implementations of
/// this interface inside registered module assemblies and drives their lifecycle:
/// construct (parameterless constructor) → <see cref="Initialize"/> → <see cref="CreateConfigPage"/>.
/// Modules must reference only this contracts assembly, never the host application, so new
/// modules can be added without any host code changes.
/// </summary>
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

    /// <summary>
    /// Builds the module's configuration tab page, injected into the dashboard by the host.
    /// Called on the UI thread after <see cref="Initialize"/>. Return a fully built
    /// <see cref="System.Windows.Forms.TabPage"/> (the host adds it to its tab control).
    /// </summary>
    System.Windows.Forms.TabPage CreateConfigPage();
}
