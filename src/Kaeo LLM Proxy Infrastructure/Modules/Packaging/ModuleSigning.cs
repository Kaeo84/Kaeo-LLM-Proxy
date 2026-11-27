using System.Security.Cryptography;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Module signing primitives: ECDSA P-256 via built-in cryptography (cross-platform, no new
/// dependencies). The signature covers the exact <c>manifest.json</c> bytes; the manifest's
/// per-file SHA-256 hashes cover the payload. Keys are PEM files: PKCS#8 private (<c>.key</c>)
/// and SubjectPublicKeyInfo public (<c>.pub</c>).
/// </summary>
internal static class ModuleSigning
{
    /// <summary>Fixed-size IEEE P1363 signature (r||s, 32 bytes each for P-256).</summary>
    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>A generated keypair: PEM texts plus the trust-list key id.</summary>
    internal sealed record GeneratedKey(string PrivateKeyPem, string PublicKeyPem, string KeyId);

    public static GeneratedKey GenerateKey()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string privatePem = ecdsa.ExportPkcs8PrivateKeyPem();
        string publicPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        string keyId = KeyIdFromPublicKeyDer(ecdsa.ExportSubjectPublicKeyInfo());
        return new GeneratedKey(privatePem, publicPem, keyId);
    }

    /// <summary>
    /// Stable key id: first 16 hex chars of SHA-256 over the DER SubjectPublicKeyInfo. The same
    /// id is shown at keygen, stored in the trust list, and reported for signed modules.
    /// </summary>
    public static string KeyIdFromPublicKeyDer(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo))[..16].ToLowerInvariant();

    /// <summary>Signs the manifest bytes with a PKCS#8 PEM private key.</summary>
    public static byte[] SignManifest(byte[] manifestBytes, string privateKeyPem)
    {
        using ECDsa ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new ModulePackageException($"The signing key could not be read: {ex.Message}", ex);
        }

        return ecdsa.SignData(manifestBytes, HashAlgorithmName.SHA256, SignatureFormat);
    }

    /// <summary>
    /// Verifies a manifest signature against one base64 DER SubjectPublicKeyInfo (the trust-list
    /// format). Returns false for malformed keys/signatures instead of throwing.
    /// </summary>
    public static bool VerifyManifest(byte[] manifestBytes, byte[] signature, string publicKeyBase64)
    {
        try
        {
            byte[] der = Convert.FromBase64String(publicKeyBase64);
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(der, out _);
            return ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, SignatureFormat);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the trusted key that verifies the signature, or null. A signature that verifies
    /// against no trusted key is untrusted (wrong key or tampered manifest).
    /// </summary>
    public static TrustedModuleKey? FindTrustedSigner(
        byte[] manifestBytes, byte[] signature, IReadOnlyList<TrustedModuleKey> trustedKeys)
    {
        foreach (TrustedModuleKey key in trustedKeys)
        {
            if (!string.IsNullOrWhiteSpace(key.PublicKey) && VerifyManifest(manifestBytes, signature, key.PublicKey))
                return key;
        }
        return null;
    }

    /// <summary>Strips PEM armor to the base64 DER body (for trust-list entries).</summary>
    public static string PemToBase64Der(string pem)
    {
        ReadOnlySpan<char> span = pem.AsSpan();
        int start = span.IndexOf('\n');
        int end = span.LastIndexOf("-----END");
        if (start < 0 || end < 0 || end <= start)
            return pem.Trim(); // already bare base64
        return new string(span[(start + 1)..end]).ReplaceLineEndings(string.Empty).Trim();
    }
}