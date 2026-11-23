using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Ambient context for the agent turn currently executing, set by the tool-window view model
/// at turn start. vs_delegate reads it to spawn a nested run with the same client/model/mode,
/// the parent's tool scope and permission handler, and the agent catalog. AsyncLocal is
/// copy-on-write across async flows: a nested delegation sees the child context while the
/// parent's flow keeps its own, so depth and scope never leak back to the caller.
/// </summary>
internal sealed class SubAgentContext
{
    private static readonly AsyncLocal<SubAgentContext?> Ambient = new();

    public static SubAgentContext? Current => Ambient.Value;

    public static void SetCurrent(SubAgentContext? context) => Ambient.Value = context;

    public IChatClient Client { get; init; } = null!;
    public string Model { get; init; } = string.Empty;
    public AgentMode Mode { get; init; }

    /// <summary>The parent turn's tool scope (null = all tools). A sub-agent never exceeds it.</summary>
    public IReadOnlyList<string>? ParentTools { get; init; }

    /// <summary>The parent's Interactive-mode permission handler; sub-agent prompts surface in the same transcript.</summary>
    public Func<ToolCallRequest, Task<bool>>? RequestPermission { get; init; }

    public IReadOnlyList<AgentConfig> Agents { get; init; } = [];
    public McpServerManager Mcp { get; init; } = null!;
    public ExtensionSettingsStore Settings { get; init; } = null!;

    /// <summary>0 for the main turn; a sub-agent runs at parent depth + 1.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// vs_delegate: runs a self-contained task in an isolated sub-agent (fresh history, own
/// tool-loop budget) and returns only its final answer, keeping noisy exploration out of the
/// main conversation. Gated by Defaults.SubAgents (off by default); depth cap 1 for v1.
/// </summary>
internal static class VsDelegateTools
{
    private const string SubAgentGuidance =
        "You are an isolated sub-agent: you have a fresh context and cannot see the parent conversation. " +
        "Complete the delegated task using the available tools, then return a concise, self-contained final " +
        "answer (what you did, what you found, files touched). Do not ask questions; make reasonable " +
        "assumptions and state them.";

    // Thread affinity: this method is invoked from the agent loop on the UI thread and must
    // stay there — the nested runtime's event callbacks (permission cards) touch UI-affine
    // collections directly, exactly like the parent turn. No ConfigureAwait(false) here.
    public static async Task<string> DelegateAsync(JsonNode? args, CancellationToken ct)
    {
        var ctx = SubAgentContext.Current;
        if (ctx is null)
            return "Tool execution failed: vs_delegate is only available during an active agent turn.";

        var defaults = (await ctx.Settings.LoadAsync()).Defaults ?? new Defaults();
        if (SubAgentPolicy.Validate(defaults.SubAgents, ctx.Depth) is { } refusal)
            return "Tool execution failed: " + refusal;

        var task = args?["task"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(task))
            return "Tool execution failed: 'task' is required — describe the self-contained job for the sub-agent.";

        var agentName = args?["agent"]?.GetValue<string>();
        AgentConfig? named = null;
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            named = ctx.Agents.FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
            if (named is null)
                return $"Tool execution failed: agent '{agentName}' not found. Available agents: {string.Join(", ", ctx.Agents.Select(a => a.Name))}.";
        }

        List<string>? requested = null;
        if (args?["tools"] is JsonArray array)
        {
            requested = [];
            foreach (var node in array)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var toolName) && !string.IsNullOrWhiteSpace(toolName))
                    requested.Add(toolName);
            }
        }

        var allTools = ctx.Mcp.GetAvailableToolDefinitions(null)
            .Select(d => d["function"]?["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        var tools = SubAgentPolicy.ResolveTools(requested ?? named?.Tools, ctx.ParentTools, allTools);

        var name = named?.Name ?? "SubAgent";
        var subAgent = new AgentConfig
        {
            Name = name,
            DisplayName = named?.DisplayName ?? "Sub-agent",
            SystemPrompt = named is null || string.IsNullOrWhiteSpace(named.SystemPrompt)
                ? SubAgentGuidance
                : named.SystemPrompt + "\n\n" + SubAgentGuidance,
            Tools = tools,
        };

        var budget = SubAgentPolicy.ResolveBudget(defaults.SubAgentBudget);

        // AutoPilot's approve-all semantics carry over, but a sub-agent must not auto-continue:
        // its iteration budget is the hard stop. Bypass approves everything without continuation.
        var nestedMode = ctx.Mode == AgentMode.AutoPilot ? AgentMode.Bypass : ctx.Mode;

        var previousContext = SubAgentContext.Current;
        var previousMode = BuiltInVsTools.CurrentMode;
        SubAgentContext.SetCurrent(new SubAgentContext
        {
            Client = ctx.Client,
            Model = ctx.Model,
            Mode = nestedMode,
            ParentTools = tools,
            RequestPermission = ctx.RequestPermission,
            Agents = ctx.Agents,
            Mcp = ctx.Mcp,
            Settings = ctx.Settings,
            Depth = ctx.Depth + 1,
        });
        try
        {
            var runtime = new AgentRuntime(ctx.Mcp, ctx.Settings);
            var result = await runtime.RunTurnAsync(
                ctx.Client, subAgent, ctx.Model, nestedMode,
                history: [], userPrompt: task!,
                events: new AgentEvents { RequestPermission = ctx.RequestPermission },
                ct: ct, maxIterationsOverride: budget);

            var status = result.Completed ? "completed" : "stopped: iteration budget exhausted";
            var text = string.IsNullOrWhiteSpace(result.FinalText) ? "(no final answer)" : result.FinalText;
            return $"Sub-agent '{name}' {status} ({result.ToolCallsExecuted} tool calls).\n\n{text}";
        }
        catch (OperationCanceledException)
        {
            // Stop pressed: unwind the parent turn too, never hand cancellation back as a result.
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool execution failed: sub-agent '{name}' failed: {ex.Message}";
        }
        finally
        {
            SubAgentContext.SetCurrent(previousContext);
            BuiltInVsTools.CurrentMode = previousMode;
        }
    }
}