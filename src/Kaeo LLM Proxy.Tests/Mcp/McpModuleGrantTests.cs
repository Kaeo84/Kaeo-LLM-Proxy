using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Mcp;

/// <summary>
/// Pins the module-filtering rule used when an MCP session is built: a session with no narrowing
/// grant sees every module's tools, a narrowed session sees only the modules it names, and a tool
/// whose owning module cannot be identified is withheld rather than silently exposed.
/// </summary>
public sealed class McpModuleGrantTests
{
    private static McpSessionInfo Session(params string[]? allowedModules) => new("session-1", "127.0.0.1")
    {
        AllowedModules = allowedModules,
    };

    [Fact]
    public void NoNarrowingGrant_AllowsEveryModule()
    {
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(Session(), "web-search"));
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(Session(), "anything-else"));

        // An empty list is the "every module" default, not "no modules".
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(Session([]), "web-search"));
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(Session([]), null));
    }

    [Fact]
    public void NarrowingGrant_AllowsOnlyTheNamedModules()
    {
        McpSessionInfo session = Session("web-search", "Notes");

        Assert.True(McpServerOptionsFactory.IsModuleAllowed(session, "web-search"));
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(session, "WEB-SEARCH"));
        Assert.True(McpServerOptionsFactory.IsModuleAllowed(session, "notes"));
        Assert.False(McpServerOptionsFactory.IsModuleAllowed(session, "code-vector"));
    }

    [Fact]
    public void NarrowingGrant_WithholdsToolsWhoseModuleIsUnknown()
    {
        McpSessionInfo session = Session("web-search");

        Assert.False(McpServerOptionsFactory.IsModuleAllowed(session, null));
        Assert.False(McpServerOptionsFactory.IsModuleAllowed(session, string.Empty));
        Assert.False(McpServerOptionsFactory.IsModuleAllowed(session, "   "));
    }

    [Fact]
    public void SessionInfo_DefaultsToEveryModule()
    {
        Assert.Null(new McpSessionInfo("session-2", null).AllowedModules);
    }
}