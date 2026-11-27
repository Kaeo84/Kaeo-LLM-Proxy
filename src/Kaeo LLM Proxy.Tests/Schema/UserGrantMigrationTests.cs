using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Schema;

/// <summary>
/// Model and MCP access moved from the API key to the user who owns it. Databases created before
/// that change still carry grants on key rows, so the startup reconcile folds them into the owning
/// user. These tests pin that upgrade path: nothing an administrator granted may be lost, and a
/// grant the administrator has since set on the user must never be overwritten.
/// </summary>
public sealed class UserGrantMigrationTests
{
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-grants-{Guid.NewGuid():N}.db");

    private static AppDatabase OpenDatabase(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    [Fact]
    public void PerKeyGrants_FoldIntoTheOwningUser_AndTheKeyColumnsAreCleared()
    {
        string path = NewDatabasePath();
        int userId;
        int keyId;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new() { Name = "Legacy Person", CreatedUtc = DateTime.UtcNow };
            database.InsertProxyUser(user);
            userId = user.Id;
            keyId = InsertKey(database, userId, "laptop");
        }

        WriteLegacyKeyGrants(path, keyId, ["alpha", "beta"], mcpAccess: true);

        using (AppDatabase upgraded = OpenDatabase(path))
        {
            ProxyUser reloaded = upgraded.FindProxyUser(userId)!;
            Assert.Equal(["alpha", "beta"], reloaded.AllowedModels);
            Assert.True(reloaded.McpAccess);

            ProxyApiKey key = upgraded.LoadProxyApiKeys().Single(k => k.Id == keyId);
            Assert.Empty(key.AllowedModels);
            Assert.False(key.McpAccess);
        }
    }

    [Fact]
    public void PerKeyGrants_FromSeveralKeys_AreUnioned()
    {
        string path = NewDatabasePath();
        int userId;
        int first;
        int second;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new() { Name = "Multi Key", CreatedUtc = DateTime.UtcNow };
            database.InsertProxyUser(user);
            userId = user.Id;
            first = InsertKey(database, userId, "laptop");
            second = InsertKey(database, userId, "ci");
        }

        WriteLegacyKeyGrants(path, first, ["alpha"], mcpAccess: false);
        WriteLegacyKeyGrants(path, second, ["beta", "Alpha"], mcpAccess: true);

        using (AppDatabase upgraded = OpenDatabase(path))
        {
            ProxyUser reloaded = upgraded.FindProxyUser(userId)!;
            // Case-insensitive union: "Alpha" adds nothing to "alpha".
            Assert.Equal(2, reloaded.AllowedModels.Count);
            Assert.Contains("alpha", reloaded.AllowedModels);
            Assert.Contains("beta", reloaded.AllowedModels);
            Assert.True(reloaded.McpAccess);
        }
    }

    [Fact]
    public void PerKeyGrants_NeverOverwriteGrantsTheUserAlreadyHas()
    {
        string path = NewDatabasePath();
        int userId;
        int keyId;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new()
            {
                Name = "Already Granted",
                CreatedUtc = DateTime.UtcNow,
                AllowedModels = ["gamma"],
            };
            database.InsertProxyUser(user);
            userId = user.Id;
            keyId = InsertKey(database, userId, "laptop");
        }

        WriteLegacyKeyGrants(path, keyId, ["alpha"], mcpAccess: true);

        using (AppDatabase upgraded = OpenDatabase(path))
        {
            ProxyUser reloaded = upgraded.FindProxyUser(userId)!;
            Assert.Equal(["gamma"], reloaded.AllowedModels);
            Assert.False(reloaded.McpAccess);

            // The legacy columns are still cleared so the fold is a one-time event.
            ProxyApiKey key = upgraded.LoadProxyApiKeys().Single(k => k.Id == keyId);
            Assert.Empty(key.AllowedModels);
            Assert.False(key.McpAccess);
        }
    }

    [Fact]
    public void PerKeyGrants_AreNotResurrectedOnASecondStart()
    {
        string path = NewDatabasePath();
        int userId;
        int keyId;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new() { Name = "Cleared Later", CreatedUtc = DateTime.UtcNow };
            database.InsertProxyUser(user);
            userId = user.Id;
            keyId = InsertKey(database, userId, "laptop");
        }

        WriteLegacyKeyGrants(path, keyId, ["alpha"], mcpAccess: true);

        using (AppDatabase upgraded = OpenDatabase(path))
            Assert.Equal(["alpha"], upgraded.FindProxyUser(userId)!.AllowedModels);

        // The administrator revokes the grant; a later start must not put it back.
        using (AppDatabase revoked = OpenDatabase(path))
        {
            ProxyUser user = revoked.FindProxyUser(userId)!;
            user.AllowedModels = [];
            user.McpAccess = false;
            revoked.UpdateProxyUser(user);
        }

        using (AppDatabase reopened = OpenDatabase(path))
        {
            ProxyUser user = reopened.FindProxyUser(userId)!;
            Assert.Empty(user.AllowedModels);
            Assert.False(user.McpAccess);
        }
    }

    [Fact]
    public void MultipleDashboardRoles_RoundTripThroughTheRoleColumn()
    {
        string path = NewDatabasePath();
        int userId;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new()
            {
                Name = "Many Hats",
                CreatedUtc = DateTime.UtcNow,
                DashboardRole = "readOnly,userAdmin",
                AllowedModels = ["alpha"],
                McpAccess = true,
            };
            database.InsertProxyUser(user);
            userId = user.Id;
        }

        using (AppDatabase reopened = OpenDatabase(path))
        {
            ProxyUser user = reopened.FindProxyUser(userId)!;
            Assert.Equal("readOnly,userAdmin", user.DashboardRole);
            Assert.Equal([DashboardRoles.ReadOnly, DashboardRoles.UserAdmin], DashboardRoles.Parse(user.DashboardRole));
            Assert.True(user.IsDashboardAdmin is false);
            Assert.True(DashboardRoles.HasCapability(user.DashboardRole, DashboardCapability.ManageUsers));
            Assert.Equal(["alpha"], user.AllowedModels);
            Assert.True(user.McpAccess);
        }
    }

    [Fact]
    public void AdminRole_SurvivesTheRoleColumnUnchanged()
    {
        string path = NewDatabasePath();
        int userId;

        using (AppDatabase database = OpenDatabase(path))
        {
            ProxyUser user = new() { Name = "Admin", CreatedUtc = DateTime.UtcNow, DashboardRole = "admin" };
            database.InsertProxyUser(user);
            userId = user.Id;
        }

        using (AppDatabase reopened = OpenDatabase(path))
            Assert.True(reopened.FindProxyUser(userId)!.IsDashboardAdmin);
    }

    private static int InsertKey(AppDatabase database, int userId, string label)
    {
        (_, string hash, string prefix) = ProxyAccessService.GenerateKey();
        ProxyApiKey key = new()
        {
            UserId = userId,
            Label = label,
            KeyHash = hash,
            KeyPrefix = prefix,
            CreatedUtc = DateTime.UtcNow,
        };
        database.InsertProxyApiKey(key);
        return key.Id;
    }

    /// <summary>
    /// Writes grants the way the previous release did: on the key row, straight through SQL, so the
    /// test exercises exactly the state an upgraded database is in.
    /// </summary>
    private static void WriteLegacyKeyGrants(string path, int keyId, string[] models, bool mcpAccess)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE proxy_api_keys SET allowed_models = $models, mcp_access = $mcp WHERE id = $id;";
        command.Parameters.AddWithValue("$models", System.Text.Json.JsonSerializer.Serialize(models));
        command.Parameters.AddWithValue("$mcp", mcpAccess ? 1 : 0);
        command.Parameters.AddWithValue("$id", keyId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}