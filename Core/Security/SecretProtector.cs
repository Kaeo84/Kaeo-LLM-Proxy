using System.Security.Cryptography;
using System.Text;

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
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Encoding.UTF8.GetString(plaintext);
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

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
}
