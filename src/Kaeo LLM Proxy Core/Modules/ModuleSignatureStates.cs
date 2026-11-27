namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Signature states surfaced for registered modules (logs, registry, dashboard). Stored as
/// strings so the registry column and JSON API need no enum mapping.
/// </summary>
public static class ModuleSignatureStates
{
    /// <summary>No manifest signature present (loose DLL or unsigned package).</summary>
    public const string Unsigned = "Unsigned";

    /// <summary>Manifest signature verified against a trusted key.</summary>
    public const string Signed = "Signed";

    /// <summary>
    /// A signature was present but failed verification (wrong key or tampered manifest).
    /// Modules never load in this state; the value exists for diagnostics/upload responses.
    /// </summary>
    public const string Invalid = "Invalid";
}