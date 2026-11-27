using System.Security.Cryptography;
using System.Text;

namespace Kaeo.LlmProxy.Core.Security;

/// <summary>
/// Reversible protection for stored proxy API keys. Keys are still authenticated by their
/// SHA-256 hash; this envelope exists only so an administrator can read a key back out of the
/// database ("reveal") or replace its material in place ("regenerate").
/// </summary>
/// <remarks>
/// The AES-256-GCM key is a random 32-byte file created beside the application database on first
/// use, so revealing needs no operator passphrase and behaves the same on Windows, macOS, and
/// Linux. That protects a database copied on its own — a backup, a sync folder, a support bundle
/// — but it cannot protect against someone who can already read the whole data folder. Values
/// are wrapped in a versioned envelope so a stored value can be recognized (and skipped) when it
/// is not encrypted.
/// </remarks>
internal sealed class ApiKeyProtector
{
    private const string EnvelopePrefix = "kaeo-key:v1:";
    private const int KeySize = 32;      // AES-256
    private const int NonceSize = 12;    // AES-GCM standard nonce size
    private const int TagSize = 16;      // AES-GCM tag size

    private readonly object _sync = new();
    private readonly string _keyFilePath;
    private byte[]? _key;

    public ApiKeyProtector(string keyFilePath) => _keyFilePath = keyFilePath;

    /// <summary>Path of the key file backing this protector.</summary>
    public string KeyFilePath => _keyFilePath;

    /// <summary>
    /// Creates a protector whose key file sits beside <paramref name="databasePath"/>, so the key
    /// travels with the data folder it protects.
    /// </summary>
    public static ApiKeyProtector ForDatabase(string databasePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";
        return new ApiKeyProtector(Path.Combine(directory, "api-key-protection.key"));
    }

    /// <summary>Returns true when <paramref name="value"/> carries an encrypted envelope.</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EnvelopePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into a storable envelope. Returns false when the key
    /// file is missing and cannot be created (for example a read-only data folder) so callers can
    /// degrade instead of failing the operation that needed the envelope.
    /// </summary>
    public bool TryEncrypt(string? plaintext, out string? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(plaintext))
            return false;

        byte[]? key = ResolveKey(createIfMissing: true);
        if (key is null)
            return false;

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
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
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        // Payload: nonce | tag | ciphertext
        byte[] payload = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        ciphertext.CopyTo(payload, NonceSize + TagSize);

        envelope = EnvelopePrefix + Convert.ToBase64String(payload);
        return true;
    }

    /// <summary>
    /// Decrypts an envelope produced by <see cref="TryEncrypt"/>. Returns false (never throws) for
    /// a missing key file, a malformed envelope, or a value this key file cannot decrypt.
    /// </summary>
    public bool TryDecrypt(string? envelope, out string? plaintext)
    {
        plaintext = null;
        if (!IsProtected(envelope))
            return false;

        byte[]? key = ResolveKey(createIfMissing: false);
        if (key is null)
            return false;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope![EnvelopePrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length <= NonceSize + TagSize)
            return false;

        byte[] nonce = payload[..NonceSize];
        byte[] tag = payload[NonceSize..(NonceSize + TagSize)];
        byte[] ciphertext = payload[(NonceSize + TagSize)..];
        byte[] plaintextBytes = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            plaintext = Encoding.UTF8.GetString(plaintextBytes);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private byte[]? ResolveKey(bool createIfMissing)
    {
        lock (_sync)
        {
            if (_key is not null)
                return _key;

            try
            {
                if (!File.Exists(_keyFilePath))
                    return createIfMissing ? _key = CreateKeyFile() : null;

                byte[] existing = Convert.FromBase64String(File.ReadAllText(_keyFilePath).Trim());
                return existing.Length == KeySize ? _key = existing : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException)
            {
                // Degrade: reveal/regenerate reports "not recoverable" instead of failing the
                // request. A sharing violation from a second app instance lands here too.
                return null;
            }
        }
    }

    private byte[]? CreateKeyFile()
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        string? directory = Path.GetDirectoryName(_keyFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            // CreateNew so two instances racing on first use cannot clobber each other's key;
            // the loser reads the winner's file in the handler below.
            using var stream = new FileStream(_keyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] text = Encoding.UTF8.GetBytes(Convert.ToBase64String(key));
            stream.Write(text, 0, text.Length);
        }
        catch (IOException) when (File.Exists(_keyFilePath))
        {
            byte[] existing = Convert.FromBase64String(File.ReadAllText(_keyFilePath).Trim());
            return existing.Length == KeySize ? existing : null;
        }

        RestrictKeyFilePermissions();
        return key;
    }

    private void RestrictKeyFilePermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(_keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: the data folder itself is already operator-controlled.
        }
    }
}