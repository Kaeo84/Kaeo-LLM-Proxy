using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
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
/// Authenticates proxy API keys and answers per-key model-access questions. Keys are stored as
/// SHA-256 hashes only; the plaintext exists once at creation. Lookups are cached briefly so a
/// busy client does not hit SQLite on every request, and the cache is invalidated whenever the
/// dashboard changes users or keys.
/// </summary>
internal sealed class ProxyAccessService(AppDatabase database)
{
    /// <summary>Prefix every generated key carries so leaked keys are recognizable in logs/scans.</summary>
    internal const string KeyPrefixMarker = "kaeo-";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromSeconds(60);

    private readonly AppDatabase _database = database;

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
    /// Whether the principal may call <paramref name="modelName"/>. No principal (open proxy) or
    /// an empty allow-list means every model; otherwise the requested name must match an entry
    /// case-insensitively.
    /// </summary>
    internal static bool IsModelAllowed(ProxyAccessPrincipal? principal, string? modelName)
    {
        if (principal is null || principal.Key.AllowedModels.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        return principal.Key.AllowedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase);
    }

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