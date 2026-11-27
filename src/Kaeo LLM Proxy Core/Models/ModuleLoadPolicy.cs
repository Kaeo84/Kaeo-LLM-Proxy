namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Enforcement policy for module loading. Every module load — loose DLL, <c>.kaeomod</c>
/// package, or dashboard upload — routes through the same verification seam, so flipping
/// this setting is config-only.
/// </summary>
public enum ModuleLoadPolicy
{
    /// <summary>Load unsigned modules without complaint (default; today's behavior).</summary>
    AllowUnsigned = 0,

    /// <summary>Load unsigned modules but log a warning for each.</summary>
    Warn = 1,

    /// <summary>
    /// Refuse unsigned modules outright. Packages must carry a manifest signature made by a
    /// key in <see cref="AppSettings.TrustedModuleKeys"/>; loose DLLs are always refused.
    /// </summary>
    RequireSigned = 2,
}

/// <summary>
/// A public key trusted to sign module packages. <see cref="PublicKey"/> is the base64
/// DER SubjectPublicKeyInfo of an ECDSA P-256 key (the contents of the <c>.pub</c> file
/// produced by <c>--module-keygen</c>, PEM headers stripped).
/// </summary>
public sealed class TrustedModuleKey
{
    /// <summary>Optional friendly name shown in logs and the dashboard.</summary>
    public string? Name { get; set; }

    /// <summary>Base64 DER SubjectPublicKeyInfo of the trusted ECDSA P-256 public key.</summary>
    public string PublicKey { get; set; } = string.Empty;
}