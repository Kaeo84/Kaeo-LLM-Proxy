using System.Security.Cryptography;
using System.Text;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Core.Security;

/// <summary>
/// Encrypts and decrypts secret strings (e.g. API keys) using AES-256-GCM with a key derived
/// from a user-supplied passphrase via PBKDF2-SHA256. Encrypted values are wrapped in a
/// versioned envelope (<c>kaeo-enc:v1:…</c>) so plaintext and encrypted values can coexist
/// during migration.
/// </summary>
internal static class SecretProtector
{
    private const string EnvelopePrefix = "kaeo-enc:v1:";

    /// <summary>
    /// Prefix for a SHA-256 hash of the user's passphrase stored in
    /// <see cref="AppSettings.SecurityPassphrase"/>. When this prefix is present the stored
    /// value is a one-way hash, not the raw passphrase. The hash itself is used as PBKDF2
    /// input so that decryption works without prompting.
    /// </summary>
    private const string HashPrefix = "!pwh!";

    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;   // AES-GCM tag size
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 100_000;

    /// <summary>Returns true when <paramref name="value"/> carries the encrypted envelope prefix.</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EnvelopePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with the supplied <paramref name="passphrase"/> and
    /// returns a versioned envelope string suitable for storage.
    /// </summary>
    public static string Encrypt(string plaintext, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(passphrase, salt);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        // Envelope payload: salt | nonce | tag | ciphertext
        byte[] payload = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        salt.CopyTo(payload, 0);
        nonce.CopyTo(payload, SaltSize);
        tag.CopyTo(payload, SaltSize + NonceSize);
        ciphertext.CopyTo(payload, SaltSize + NonceSize + TagSize);

        return EnvelopePrefix + Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a value previously produced by <see cref="Encrypt"/>. Returns the original
    /// plaintext, or throws <see cref="CryptographicException"/> when the passphrase is wrong
    /// or the data has been tampered with.
    /// </summary>
    public static string Decrypt(string envelope, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(envelope);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        if (!IsEncrypted(envelope))
            throw new ArgumentException("Value is not an encrypted envelope.", nameof(envelope));

        byte[] payload = Convert.FromBase64String(envelope[EnvelopePrefix.Length..]);

        if (payload.Length < SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Encrypted payload is too short.");

        byte[] salt = payload[..SaltSize];
        byte[] nonce = payload[SaltSize..(SaltSize + NonceSize)];
        byte[] tag = payload[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
        byte[] ciphertext = payload[(SaltSize + NonceSize + TagSize)..];

        byte[] key = DeriveKey(passphrase, salt);
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            // Scrub the decrypted bytes too; the returned string is immutable, but the buffer
            // must not linger in memory until GC.
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Attempts to decrypt <paramref name="envelope"/>. Returns true and sets
    /// <paramref name="plaintext"/> on success; returns false when the passphrase is wrong
    /// or the data is corrupt.
    /// </summary>
    public static bool TryDecrypt(string envelope, string passphrase, out string? plaintext)
    {
        try
        {
            plaintext = Decrypt(envelope, passphrase);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = null;
            return false;
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passphraseBytes,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }
        finally
        {
            // The passphrase string itself is immutable and cannot be scrubbed, but its UTF-8
            // copy must not linger on the heap until the GC gets around to it.
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    /// <summary>
    /// Returns the SHA-256 hash of <paramref name="passphrase"/> as a Base64 string
    /// prefixed with <c>!pwh!</c> so it can be distinguished from a raw passphrase stored
    /// in <see cref="AppSettings.SecurityPassphrase"/>.
    /// </summary>
    internal static string HashPassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] hash;
        try
        {
            hash = SHA256.HashData(passphraseBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }

        return HashPrefix + Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> is a hashed passphrase (prefixed with
    /// <c>!pwh!</c>), as opposed to a legacy raw passphrase.
    /// </summary>
    internal static bool IsHashedPassphrase(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(HashPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Strips the <c>!pwh!</c> prefix from a hashed passphrase, returning the raw Base64
    /// SHA-256 hash suitable for use as PBKDF2 key material.
    /// </summary>
    internal static string StripHashPrefix(string hashed)
    {
        if (!IsHashedPassphrase(hashed))
            throw new ArgumentException("Value is not a hashed passphrase.", nameof(hashed));
        return hashed[HashPrefix.Length..];
    }

    /// <summary>
    /// Resolves the effective passphrase material to use for encryption/decryption.
    /// If <paramref name="stored"/> is already hashed (<c>!pwh!</c>), returns the hash
    /// stripped of its prefix. If it is a legacy raw passphrase, returns it as-is.
    /// </summary>
    internal static string ResolvePassphraseMaterial(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;
        return IsHashedPassphrase(stored) ? StripHashPrefix(stored) : stored;
    }

    /// <summary>
    /// The three field names on <see cref="StoredCredential"/> that carry secret material.
    /// </summary>
    internal static string[] SecretMaterialFields { get; } = ["Secret", "PrivateKey", "Certificate"];

    /// <summary>
    /// Returns true when any of the secret-bearing fields on <paramref name="credential"/>
    /// carry an encrypted envelope.
    /// </summary>
    internal static bool HasAnyEncryptedSecret(StoredCredential credential)
    {
        foreach (string? value in EnumerateSecretMaterial(credential))
        {
            if (IsEncrypted(value))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Yields the three secret-material values (Secret, PrivateKey, Certificate) from
    /// <paramref name="credential"/>, any of which may be null.
    /// </summary>
    internal static IEnumerable<string?> EnumerateSecretMaterial(StoredCredential credential)
    {
        yield return credential.Secret;
        yield return credential.PrivateKey;
        yield return credential.Certificate;
    }
}
