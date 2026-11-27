using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// The authenticated identity behind one proxy request: the owning user plus the specific key
/// that authenticated. Carried per-request (AsyncLocal) so model-access checks and discovery
/// filtering see the right principal even under concurrent requests.
/// </summary>
internal sealed record ProxyAccessPrincipal(ProxyUser User, ProxyApiKey Key);

/// <summary>
/// Authenticates proxy API keys and answers per-key model-access questions. Requests are
/// matched against a SHA-256 hash; the plaintext additionally sits in the row under reversible
/// encryption so an administrator can reveal or regenerate the key. Lookups are cached briefly
/// so a busy client does not hit SQLite on every request, and the cache is invalidated whenever
/// the dashboard changes users or keys.
/// </summary>
internal sealed class ProxyAccessService(AppDatabase database, ApiKeyProtector? keyProtector = null)
{
    /// <summary>Prefix every generated key carries so leaked keys are recognizable in logs/scans.</summary>
    internal const string KeyPrefixMarker = "kaeo-";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromSeconds(60);

    private readonly AppDatabase _database = database;

    // Reversible envelope for stored keys. Defaults to a key file beside the application
    // database so reveal/regenerate work with no extra configuration.
    private readonly ApiKeyProtector _keyProtector = keyProtector ?? ApiKeyProtector.ForDatabase(database.DatabasePath);

    // Hash → (principal or null for "unknown key", load time). Negative results are cached too so
    // a client spraying invalid keys cannot turn every request into a database lookup.
    private readonly ConcurrentDictionary<string, (ProxyAccessPrincipal? Principal, DateTime LoadedUtc)> _cache = new(StringComparer.Ordinal);

    // Key id → last time LastUsedUtc was persisted, to throttle the write to once a minute.
    private readonly ConcurrentDictionary<int, DateTime> _lastUsedWrites = new();

    /// <summary>
    /// Generates a fresh API key: "kaeo-" + 48 lowercase hex characters (24 random bytes).
    /// The plaintext is returned to the caller exactly once; only the hash is persisted.
    /// </summary>
    internal static (string PlainText, string Hash, string Prefix) GenerateKey()
    {
        string plainText = KeyPrefixMarker + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        return (plainText, HashKey(plainText), plainText[..13]);
    }

    /// <summary>Lowercase hex SHA-256 of the full plaintext key.</summary>
    internal static string HashKey(string plainText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plainText)));

    /// <summary>
    /// Generates fresh key material: the plaintext handed to the caller, the hash requests are
    /// authenticated against, the display prefix, and the reversibly-protected copy that lets an
    /// administrator reveal the key later (null when the protection key file is unavailable).
    /// </summary>
    internal (string PlainText, string Hash, string Prefix, string? Secret) GenerateKeyMaterial()
    {
        (string plainText, string hash, string prefix) = GenerateKey();
        return (plainText, hash, prefix, ProtectKey(plainText));
    }

    /// <summary>Encrypts a plaintext key for storage; null when protection is unavailable.</summary>
    internal string? ProtectKey(string plainText)
    {
        if (_keyProtector.TryEncrypt(plainText, out string? envelope))
            return envelope;

        Log.Warning(
            "Could not protect a new API key for storage ({KeyFile} is unavailable); the key will not be revealable",
            _keyProtector.KeyFilePath);
        return null;
    }

    /// <summary>
    /// Recovers a stored key's plaintext, or null when the key predates stored secrets, the
    /// protection key file is missing, or the envelope cannot be decrypted. Callers should offer
    /// regeneration in that case.
    /// </summary>
    internal string? RevealKey(ProxyApiKey key)
    {
        if (string.IsNullOrEmpty(key.KeySecret))
            return null;

        if (_keyProtector.TryDecrypt(key.KeySecret, out string? plainText))
            return plainText;

        Log.Warning(
            "Could not decrypt stored API key {KeyId}; the protection key file may have been replaced",
            key.Id);
        return null;
    }

    /// <summary>
    /// Resolves a presented plaintext key to its principal, or null when the key is unknown or
    /// its user is disabled. A database failure logs a warning and returns null (fail closed
    /// when key enforcement is on; anonymous when it is off).
    /// </summary>
    public ProxyAccessPrincipal? Authenticate(string? plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
            return null;

        string hash = HashKey(plainTextKey);
        DateTime now = DateTime.UtcNow;

        if (_cache.TryGetValue(hash, out var cached) && now - cached.LoadedUtc < CacheTtl)
        {
            if (cached.Principal is not null)
                TouchLastUsed(cached.Principal.Key, now);
            return cached.Principal;
        }

        ProxyAccessPrincipal? principal = null;
        try
        {
            ProxyApiKey? key = _database.FindProxyApiKeyByHash(hash);
            if (key is not null)
            {
                ProxyUser? user = _database.FindProxyUser(key.UserId);
                if (user is { IsEnabled: true })
                    principal = new ProxyAccessPrincipal(user, key);
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Proxy API key lookup failed; treating the request as unauthenticated");
            return null;
        }

        _cache[hash] = (principal, now);
        if (principal is not null)
            TouchLastUsed(principal.Key, now);

        return principal;
    }

    /// <summary>
    /// Whether the principal may call <paramref name="modelName"/>. Open-proxy requests and Admin
    /// users may call every model; other users need an explicit per-key model grant.
    /// </summary>
    internal static bool IsModelAllowed(ProxyAccessPrincipal? principal, string? modelName)
    {
        if (principal is null || IsAdmin(principal))
            return true;

        return !string.IsNullOrWhiteSpace(modelName)
            && principal.Key.AllowedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the principal may use the MCP server. Open-proxy requests and Admin users bypass the key grant.</summary>
    internal static bool IsMcpAllowed(ProxyAccessPrincipal? principal) =>
        principal is null || IsAdmin(principal) || principal.Key.McpAccess;

    private static bool IsAdmin(ProxyAccessPrincipal principal) =>
        string.Equals(principal.User.DashboardRole, DashboardAdminRole, StringComparison.OrdinalIgnoreCase);

    private const string DashboardAdminRole = "admin";

    /// <summary>Drops all cached authentications so dashboard edits take effect immediately.</summary>
    public void InvalidateCache()
    {
        _cache.Clear();
        _lastUsedWrites.Clear();
    }

    private void TouchLastUsed(ProxyApiKey key, DateTime now)
    {
        if (_lastUsedWrites.TryGetValue(key.Id, out DateTime lastWrite) && now - lastWrite < LastUsedWriteInterval)
            return;

        _lastUsedWrites[key.Id] = now;
        try
        {
            _database.UpdateProxyApiKeyLastUsed(key.Id, now);
            key.LastUsedUtc = now;
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Could not record API key last-used time for key {KeyId}", key.Id);
        }
    }
}