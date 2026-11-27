using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Pins the route-to-capability table the dashboard middleware enforces. Every dashboard tab calls
/// some slice of this API, so a wrong entry is either a hidden tab (too strict) or an unprotected
/// endpoint (too loose) — and the fallback must stay Admin so a newly added route is never exposed
/// by omission.
/// </summary>
public sealed class DashboardCapabilityRoutingTests
{
    private static DashboardCapability Required(string path, string method = "GET")
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Request.Method = method;
        return DashboardAuth.RequiredCapability(context.Request);
    }

    [Fact]
    public void RequiredCapability_MapsEachRouteToItsRole()
    {
        // Path, method, expected capability. The last two entries pin the fail-closed fallback:
        // an endpoint nobody has classified yet requires Admin.
        (string Path, string Method, DashboardCapability Expected)[] cases =
        [
            ("/api/users", "GET", DashboardCapability.ManageUsers),
            ("/api/users", "POST", DashboardCapability.ManageUsers),
            ("/api/users/3", "PUT", DashboardCapability.ManageUsers),
            ("/api/users/3/keys", "POST", DashboardCapability.ManageUsers),
            ("/api/keys/7/reveal", "GET", DashboardCapability.ManageUsers),
            ("/api/keys/7/regenerate", "POST", DashboardCapability.ManageUsers),
            ("/api/mappings", "GET", DashboardCapability.None),
            ("/api/mappings", "POST", DashboardCapability.ManageModels),
            ("/api/mappings/2", "DELETE", DashboardCapability.ManageModels),
            ("/api/proxy/start", "POST", DashboardCapability.DashboardControl),
            ("/api/proxy/stop", "POST", DashboardCapability.DashboardControl),
            ("/api/webdashboard/restart", "POST", DashboardCapability.DashboardControl),
            ("/api/mcp/start", "POST", DashboardCapability.DashboardControl),
            ("/api/mcp", "GET", DashboardCapability.ViewDashboard),
            ("/api/testchat/models", "GET", DashboardCapability.TestConsole),
            ("/api/testchat", "POST", DashboardCapability.TestConsole),
            ("/api/logs", "GET", DashboardCapability.ViewLogs),
            ("/api/logs/detail", "GET", DashboardCapability.ViewLogs),
            ("/api/syslog", "GET", DashboardCapability.Admin),
            ("/api/streams", "GET", DashboardCapability.Admin),
            ("/api/streams/detail", "GET", DashboardCapability.Admin),
            ("/api/usage", "GET", DashboardCapability.ViewUsage),
            ("/api/status", "GET", DashboardCapability.ViewDashboard),
            ("/api/stats", "GET", DashboardCapability.ViewDashboard),
            ("/api/settings", "GET", DashboardCapability.ManageSettings),
            ("/api/credentials", "GET", DashboardCapability.ManageSettings),
            ("/api/modules", "GET", DashboardCapability.ManageSettings),
            ("/api/auth/roles", "GET", DashboardCapability.None),
            ("/api/somethingNew", "GET", DashboardCapability.Admin),
            ("/api/somethingNew", "POST", DashboardCapability.Admin),
        ];

        foreach ((string path, string method, DashboardCapability expected) in cases)
            Assert.Equal(expected, Required(path, method));
    }
    [Fact]
    public void RequiredCapability_IsCaseInsensitive()
    {
        Assert.Equal(DashboardCapability.ManageUsers, Required("/API/Users"));
        Assert.Equal(DashboardCapability.ManageModels, Required("/Api/Mappings", "PUT"));
    }

    [Fact]
    public void RequiredCapability_TreatsHeadLikeRead()
    {
        Assert.Equal(DashboardCapability.None, Required("/api/mappings", "HEAD"));
        Assert.Equal(DashboardCapability.ManageModels, Required("/api/mappings", "PATCH"));
    }

    [Fact]
    public void Authorize_ExplainsWhichRoleIsMissing()
    {
        DefaultHttpContext context = new();
        context.Items[DashboardAuth.RoleItemKey] = DashboardRoles.ReadOnly;

        Assert.Null(DashboardAuth.Authorize(context, DashboardCapability.None));
        Assert.Null(DashboardAuth.Authorize(context, DashboardCapability.ViewLogs));

        string? denied = DashboardAuth.Authorize(context, DashboardCapability.ManageUsers);
        Assert.NotNull(denied);
        Assert.Contains("User Admin", denied);
        Assert.Contains("Read Only", denied);

        Assert.Equal("This action requires the Admin role.",
            DashboardAuth.Authorize(context, DashboardCapability.Admin));
    }

    [Fact]
    public void Authorize_UnionsMultipleRoles()
    {
        DefaultHttpContext context = new();
        context.Items[DashboardAuth.RoleItemKey] = $"{DashboardRoles.UserAdmin},{DashboardRoles.TestConsole}";

        Assert.Null(DashboardAuth.Authorize(context, DashboardCapability.ManageUsers));
        Assert.Null(DashboardAuth.Authorize(context, DashboardCapability.TestConsole));
        Assert.NotNull(DashboardAuth.Authorize(context, DashboardCapability.ManageModels));
        Assert.True(DashboardAuth.Can(context, DashboardCapability.ViewLogs));
        Assert.False(DashboardAuth.IsAdmin(context));
    }

    [Fact]
    public void Authorize_AnonymousSession_HoldsNothing()
    {
        DefaultHttpContext context = new();

        Assert.Equal(DashboardCapability.None, DashboardAuth.Capabilities(context));
        Assert.False(DashboardAuth.Can(context, DashboardCapability.ViewLogs));
        Assert.False(DashboardAuth.IsAdmin(context));
        Assert.NotNull(DashboardAuth.Authorize(context, DashboardCapability.ViewLogs));
    }
}