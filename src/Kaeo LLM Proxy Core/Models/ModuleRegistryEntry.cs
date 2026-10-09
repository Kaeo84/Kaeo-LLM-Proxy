namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// A user-registered module persisted in the <c>module_registry</c> table. Modules are added
/// explicitly via browse-to-import — the host never scans directories for modules.
/// </summary>
internal sealed class ModuleRegistryEntry
{
    /// <summary>Registry row id.</summary>
    public int Id { get; set; }

    /// <summary>Absolute path of the module assembly on disk.</summary>
    public string AssemblyPath { get; set; } = string.Empty;

    /// <summary>Stable module id reported by the module (e.g. "kaeo.mcp").</summary>
    public string? ModuleId { get; set; }

    /// <summary>Display name reported by the module.</summary>
    public string? Name { get; set; }

    /// <summary>Version reported by the module.</summary>
    public string? Version { get; set; }

    /// <summary>Whether the module is loaded at startup.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>When the module was registered (UTC).</summary>
    public DateTime RegisteredUtc { get; set; }

    /// <summary>Last load/initialization error, if any; null when the module loads cleanly.</summary>
    public string? LastError { get; set; }
}
