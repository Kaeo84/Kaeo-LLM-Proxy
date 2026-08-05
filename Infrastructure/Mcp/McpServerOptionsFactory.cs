using System.Reflection;
using Kaeo.LlmProxy.Infrastructure.Modules;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Infrastructure.Mcp;

/// <summary>
/// Builds fresh <see cref="McpServerOptions"/> for each new MCP session: server info plus every
/// <c>[McpServerTool]</c> method found on the tool targets contributed by loaded modules.
/// Targets read their own enabled/disabled state live at invocation time, so on-the-fly toggling
/// works without rebuilding the server.
/// </summary>
internal sealed class McpServerOptionsFactory(ModuleHost moduleHost)
{
    public const string ServerName = "Kaeo LLM Proxy MCP";
    public const string ServerVersion = "1.0.0";

    private readonly ModuleHost _moduleHost = moduleHost;

    public McpServerOptions Build()
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion },
            ServerInstructions =
                "Provides tools contributed by loaded modules; use tools/list to discover them.",
        };

        foreach (object target in _moduleHost.GetMcpToolTargets())
        {
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
                    continue;

                McpServerPrimitiveCollection<McpServerTool> toolCollection =
                    options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
                toolCollection.Add(McpServerTool.Create(method, target));
            }
        }

        return options;
    }
}
