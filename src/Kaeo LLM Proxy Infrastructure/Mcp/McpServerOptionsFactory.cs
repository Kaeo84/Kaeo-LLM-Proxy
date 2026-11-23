using System.Reflection;
using System.Runtime.CompilerServices;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

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

    // Tool descriptors cached per target type. A ConditionalWeakTable keeps the cache from
    // rooting module types (and their collectible load contexts) after a module is unloaded.
    private static readonly ConditionalWeakTable<Type, MethodInfo[]> _toolMethodCache = [];

    /// <summary>
    /// Builds options for the session described by <paramref name="session"/>, handing the
    /// session info to each module so targets can attribute activity to the calling client.
    /// </summary>
    public McpServerOptions Build(McpSessionInfo session)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion },
            ServerInstructions =
                "MCP server of the Kaeo LLM Proxy host. All tools are contributed by loaded " +
                "modules (for example web_search/web_fetch for web search, ssh_* for remote " +
                "command execution, and code_* for the code vector store); the exact tool set " +
                "depends on which modules are installed and enabled. Call tools/list to " +
                "discover the available tools - each tool's description and input schema " +
                "explain when and how to call it.",
        };

        // L27: track registered tool names so a cross-module duplicate is reported explicitly
        // (naming the conflict) rather than silently first-wins via the collection's Add throw.
        // Descriptors are not cached across sessions: McpServerTool.Create binds the per-session
        // target instance and the SDK exposes no target-rebinding API.
        HashSet<string> registeredToolNames = new(StringComparer.Ordinal);

        foreach (object target in _moduleHost.GetMcpToolTargets(session))
        {
            MethodInfo[] methods;
            try
            {
                methods = _toolMethodCache.GetValue(target.GetType(), static type =>
                    [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)]);
            }
            catch (Exception ex)
            {
                // Module-contributed targets live in foreign load contexts; reflection over them
                // can throw (e.g. attributes whose types failed to unify). Skip the bad target
                // instead of failing the whole MCP session build.
                Log.Warning(ex, "Failed to enumerate MCP tools on target {Target}; skipping it.", target.GetType().FullName);
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                try
                {
                    string toolName = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
                    if (!registeredToolNames.Add(toolName))
                    {
                        Log.Warning(
                            "Duplicate MCP tool name '{Tool}' contributed by {Target}; keeping the first registration and skipping this one. " +
                            "Rename the tool or disable one of the conflicting modules to resolve the conflict.",
                            toolName, target.GetType().FullName);
                        continue;
                    }

                    McpServerPrimitiveCollection<McpServerTool> toolCollection =
                        options.ToolCollection ??= [];
                    toolCollection.Add(McpServerTool.Create(method, target));
                }
                catch (Exception ex)
                {
                    // One bad tool must not skip the target's remaining tools.
                    Log.Warning(ex, "Failed to add MCP tool {Tool} from target {Target}; skipping it.", method.Name, target.GetType().FullName);
                }
            }
        }

        return options;
    }
}
