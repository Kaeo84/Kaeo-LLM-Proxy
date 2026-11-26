using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Unit tests for proxy access security: user/key persistence in AppDatabase, key generation
/// and hashing, authentication (valid / unknown / disabled-user / deleted-key), and the per-key
/// model allow-list semantics.
/// </summary>
[Collection("AppLogger")]
public sealed class ProxyAccessServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _database;
    private readonly ProxyAccessService _access;

    public ProxyAccessServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-access-{Guid.NewGuid():N}.db");
        LoggingSettings logging = new() { ApplicationDatabasePath = _dbPath };
        _database = new AppDatabase(logging);
        _access = new ProxyAccessService(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (string path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private ProxyUser CreateUser(string name = "Alice", string email = "alice@example.com", bool enabled = true, string role = "readOnly")
    {
        ProxyUser user = new() { Name = name, Email = email, CreatedUtc = DateTime.UtcNow, IsEnabled = enabled, DashboardRole = role };
        _database.InsertProxyUser(user);
        return user;
    }

    private (ProxyApiKey Key, string PlainText) CreateKey(int userId, List<string>? allowedModels = null, bool mcpAccess = false)
    {
        (string plainText, string hash, string prefix) = ProxyAccessService.GenerateKey();
        ProxyApiKey key = new()
        {
            UserId = userId,
            Label = "test",
            KeyHash = hash,
            KeyPrefix = prefix,
            CreatedUtc = DateTime.UtcNow,
            AllowedModels = allowedModels ?? [],
            McpAccess = mcpAccess,
        };
        _database.InsertProxyApiKey(key);
        _access.InvalidateCache();
        return (key, plainText);
    }

    [Fact]
    public void GenerateKey_HasKaeoPrefix_AndHashMatches()
    {
        (string plainText, string hash, string prefix) = ProxyAccessService.GenerateKey();

        Assert.StartsWith("kaeo-", plainText);
        Assert.Equal(5 + 48, plainText.Length);
        Assert.Matches("^kaeo-[0-9a-f]{48}$", plainText);
        Assert.Equal(ProxyAccessService.HashKey(plainText), hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(plainText[..13], prefix);
    }

    [Fact]
    public void GenerateKey_ProducesUniqueKeys()
    {
        var keys = Enumerable.Range(0, 32).Select(_ => ProxyAccessService.GenerateKey().PlainText).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void UserCrud_RoundTrips_AndDeleteCascadesKeys()
    {
        ProxyUser user = CreateUser();
        Assert.True(user.Id > 0);
        (ProxyApiKey key, _) = CreateKey(user.Id);

        ProxyUser loaded = Assert.Single(_database.LoadProxyUsers());
        Assert.Equal("Alice", loaded.Name);
        Assert.Equal("alice@example.com", loaded.Email);
        Assert.True(loaded.IsEnabled);

        loaded.Name = "Alicia";
        loaded.IsEnabled = false;
        _database.UpdateProxyUser(loaded);
        ProxyUser updated = _database.FindProxyUser(user.Id)!;
        Assert.Equal("Alicia", updated.Name);
        Assert.False(updated.IsEnabled);

        _database.DeleteProxyUser(user.Id);
        Assert.Empty(_database.LoadProxyUsers());
        Assert.Empty(_database.LoadProxyApiKeys());
        Assert.Null(_database.FindProxyApiKeyByHash(key.KeyHash));
    }

    [Fact]
    public void ApiKey_AllowedModels_RoundTripThroughJsonColumn()
    {
        ProxyUser user = CreateUser();
        (ProxyApiKey key, _) = CreateKey(user.Id, ["alpha", "Beta"]);

        ProxyApiKey loaded = _database.FindProxyApiKeyByHash(key.KeyHash)!;
        Assert.Equal(["alpha", "Beta"], loaded.AllowedModels);
        Assert.False(loaded.McpAccess);

        _database.UpdateProxyApiKeyAccess(key.Id, "renamed", ["gamma"], mcpAccess: true);
        ProxyApiKey updated = _database.FindProxyApiKeyByHash(key.KeyHash)!;
        Assert.Equal("renamed", updated.Label);
        Assert.Equal(["gamma"], updated.AllowedModels);
        Assert.True(updated.McpAccess);

        _database.UpdateProxyApiKeyAccess(key.Id, "renamed", []);
        ProxyApiKey cleared = _database.FindProxyApiKeyByHash(key.KeyHash)!;
        Assert.Empty(cleared.AllowedModels);
        Assert.False(cleared.McpAccess);
    }

    [Fact]
    public void Authenticate_ValidKey_ReturnsPrincipal_AndRecordsLastUsed()
    {
        ProxyUser user = CreateUser();
        (ProxyApiKey key, string plainText) = CreateKey(user.Id);
        Assert.Null(key.LastUsedUtc);

        ProxyAccessPrincipal? principal = _access.Authenticate(plainText);

        Assert.NotNull(principal);
        Assert.Equal(user.Id, principal.User.Id);
        Assert.Equal(key.Id, principal.Key.Id);
        Assert.NotNull(_database.FindProxyApiKeyByHash(key.KeyHash)!.LastUsedUtc);
    }

    [Fact]
    public void Authenticate_UnknownOrEmptyKey_ReturnsNull()
    {
        CreateUser();
        Assert.Null(_access.Authenticate(null));
        Assert.Null(_access.Authenticate(""));
        Assert.Null(_access.Authenticate("kaeo-" + new string('0', 48)));
    }

    [Fact]
    public void Authenticate_DisabledUser_ReturnsNull()
    {
        ProxyUser user = CreateUser(enabled: false);
        (_, string plainText) = CreateKey(user.Id);

        Assert.Null(_access.Authenticate(plainText));
    }

    [Fact]
    public void Authenticate_DisablingUser_TakesEffectAfterCacheInvalidation()
    {
        ProxyUser user = CreateUser();
        (_, string plainText) = CreateKey(user.Id);
        Assert.NotNull(_access.Authenticate(plainText));

        user.IsEnabled = false;
        _database.UpdateProxyUser(user);
        _access.InvalidateCache();

        Assert.Null(_access.Authenticate(plainText));
    }

    [Fact]
    public void Authenticate_DeletedKey_ReturnsNull()
    {
        ProxyUser user = CreateUser();
        (ProxyApiKey key, string plainText) = CreateKey(user.Id);
        Assert.NotNull(_access.Authenticate(plainText));

        _database.DeleteProxyApiKey(key.Id);
        _access.InvalidateCache();

        Assert.Null(_access.Authenticate(plainText));
    }

    [Fact]
    public void IsModelAllowed_EmptyAllowList_DeniesReadOnlyKeys()
    {
        ProxyUser user = CreateUser();
        (ProxyApiKey key, _) = CreateKey(user.Id);
        ProxyAccessPrincipal principal = new(user, key);

        Assert.True(ProxyAccessService.IsModelAllowed(null, "anything"));
        Assert.False(ProxyAccessService.IsModelAllowed(principal, "anything"));
        Assert.False(ProxyAccessService.IsModelAllowed(principal, ""));
    }

    [Fact]
    public void IsModelAllowed_RestrictedKey_MatchesCaseInsensitively()
    {
        ProxyUser user = CreateUser();
        (ProxyApiKey key, _) = CreateKey(user.Id, ["Alpha"]);
        ProxyAccessPrincipal principal = new(user, key);

        Assert.True(ProxyAccessService.IsModelAllowed(principal, "alpha"));
        Assert.True(ProxyAccessService.IsModelAllowed(principal, "ALPHA"));
        Assert.False(ProxyAccessService.IsModelAllowed(principal, "beta"));
        Assert.False(ProxyAccessService.IsModelAllowed(principal, ""));
        Assert.False(ProxyAccessService.IsModelAllowed(principal, null));
    }

    [Fact]
    public void IsModelAllowed_AdminKey_BypassesGrants()
    {
        ProxyUser user = CreateUser(role: "admin");
        (ProxyApiKey key, _) = CreateKey(user.Id);
        ProxyAccessPrincipal principal = new(user, key);

        Assert.True(ProxyAccessService.IsModelAllowed(principal, "anything"));
    }

    [Fact]
    public void IsMcpAllowed_RequiresGrantUnlessAdminOrAnonymous()
    {
        ProxyUser readOnly = CreateUser();
        (ProxyApiKey deniedKey, _) = CreateKey(readOnly.Id);
        (ProxyApiKey grantedKey, _) = CreateKey(readOnly.Id, mcpAccess: true);

        Assert.True(ProxyAccessService.IsMcpAllowed(null));
        Assert.False(ProxyAccessService.IsMcpAllowed(new ProxyAccessPrincipal(readOnly, deniedKey)));
        Assert.True(ProxyAccessService.IsMcpAllowed(new ProxyAccessPrincipal(readOnly, grantedKey)));

        ProxyUser admin = CreateUser(name: "Boss", email: "boss@example.com", role: "admin");
        (ProxyApiKey adminKey, _) = CreateKey(admin.Id);
        Assert.True(ProxyAccessService.IsMcpAllowed(new ProxyAccessPrincipal(admin, adminKey)));
    }
}