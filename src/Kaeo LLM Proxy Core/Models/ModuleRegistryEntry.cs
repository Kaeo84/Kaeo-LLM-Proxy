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

    /// <summary>
    /// Signature state at the last successful verification: Unsigned, Signed, or Invalid
    /// (see <see cref="Kaeo.LlmProxy.Core.Modules.ModuleSignatureStates"/>).
    /// </summary>
    public string SignatureState { get; set; } = Kaeo.LlmProxy.Core.Modules.ModuleSignatureStates.Unsigned;

    /// <summary>Key id (SHA-256 prefix of the signer's public key) when signed; otherwise null.</summary>
    public string? SignerKeyId { get; set; }
}
