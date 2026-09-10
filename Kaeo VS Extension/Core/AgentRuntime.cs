using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Execution mode controlling how tool permission requests are handled.
/// Mirrors the Copilot SDK's permission-handler patterns:
/// Bypass = approve all, Interactive = ask the user per tool, AutoPilot = approve + auto-continue.
/// </summary>
internal enum AgentMode
{
    Interactive,
    Bypass,
    AutoPilot
}

/// <summary>
/// Configuration for a single agent (built-in or user-defined). Maps to the JSONC "agents" entries.
/// </summary>
internal sealed class AgentConfig
{
    public string Name { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string SystemPrompt { get; init; } = string.Empty;
    /// <summary>Tool names this agent may use. Null = all tools.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    public string? DefaultModel { get; init; }
    /// <summary>Whether this agent is a built-in (Agent/Ask/Plan) or user-defined.</summary>
    public bool IsBuiltin { get; init; }
}

/// <summary>
/// A single message in the conversation history. <see cref="ToolCalls"/> holds the Ollama
/// <c>message.tool_calls</c> array on an assistant turn; <see cref="ToolCallId"/> ties a
/// <c>role:"tool"</c> result back to the call it answers.
/// </summary>
internal sealed record AgentMessage(string Role, string Content, JsonNode? ToolCalls = null, string? ToolCallId = null);

/// <summary>
/// A tool call the model has requested.
/// </summary>
internal sealed record ToolCallRequest(string Id, string Name, JsonNode? Arguments);

/// <summary>
/// Event bag emitted by the agent runtime during a turn.
/// Uses delegate properties (not C# events) so the runtime can raise them from a separate type.
/// </summary>
internal sealed class AgentEvents
{
    /// <summary>Streamed text delta from the model.</summary>
    public Action<string>? TextDelta;
    /// <summary>Reasoning/thinking delta (if the model supports it).</summary>
    public Action<string>? ReasoningDelta;
    /// <summary>A tool call has been requested by the model.</summary>
    public Action<ToolCallRequest>? ToolCallStart;
    /// <summary>A tool call has completed.</summary>
    public Action<ToolCallRequest, bool, string?>? ToolCallComplete;
    /// <summary>Permission request for a tool (Interactive mode only). Return true to approve.</summary>
    public Func<ToolCallRequest, Task<bool>>? RequestPermission;
    /// <summary>AutoPilot continuation requested (model signaled "not done").</summary>
    public Action<int>? AutoPilotContinuing;
    /// <summary>The turn has completed (final message or error).</summary>
    public Action<AgentTurnResult>? TurnComplete;
    /// <summary>An error occurred.</summary>
    public Action<Exception>? Error;
}

/// <summary>
/// Result of a single agent turn.
/// </summary>
internal sealed record AgentTurnResult(string FinalText, bool Completed, int ToolCallsExecuted, bool AutoPilotContinued);

/// <summary>
/// Custom agent runtime that follows the Copilot SDK's architectural patterns
/// (sessions, mode strategies, permission handlers, streaming, tool loop, compaction)
/// but runs entirely through the Kaeo LLM Proxy's Ollama-compatible API and MCP clients.
/// No external SDK dependency.
/// </summary>
internal sealed class AgentRuntime
{
    private readonly McpServerManager _mcp;
    private readonly ExtensionSettingsStore _settings;

    /// <summary>Maximum tool-call iterations per turn (prevents infinite tool loops).</summary>
    private const int MaxToolIterations = 10;

    /// <summary>Default AutoPilot continuation budget.</summary>
    private const int DefaultAutoPilotBudget = 5;

    public AgentRuntime(McpServerManager mcp, ExtensionSettingsStore settings)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Runs a single agent turn against the given upstream: streams the model response,
    /// executes any requested tool calls, feeds results back, and repeats until the model produces
    /// a final answer or the iteration budget is exhausted. Emits events via <paramref name="events"/>.
    /// </summary>
    public async Task<AgentTurnResult> RunTurnAsync(
        IUpstreamClient upstream,
        AgentConfig agent,
        string model,
        AgentMode mode,
        List<AgentMessage> history,
        string userPrompt,
        AgentEvents events,
        CancellationToken ct = default)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (events is null) throw new ArgumentNullException(nameof(events));

        history.Add(new AgentMessage("user", userPrompt));
        var defaults = (await _settings.LoadAsync().ConfigureAwait(false)).Defaults ?? new Defaults();
        var maxIterations = defaults.MaxToolIterations > 0 ? defaults.MaxToolIterations : MaxToolIterations;
        var toolCallsExecuted = 0;
        var autoPilotContinued = false;
        var autopilotBudget = DefaultAutoPilotBudget;

        string? finalText = null;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // Build the request payload for the proxy's /api/chat.
            var payload = await BuildChatPayload(agent, model, history, mode, defaults.DefaultTemperature);

            var streamedText = new List<string>();
            JsonNode? toolCallsNode = null;

            // Stream the model response.
            await foreach (var chunk in upstream.StreamChatAsync(payload, ct))
            {
                if (chunk.Text is not null)
                {
                    streamedText.Add(chunk.Text);
                    events.TextDelta?.Invoke(chunk.Text);
                }

                if (chunk.ToolCalls is not null)
                    toolCallsNode = chunk.ToolCalls;
            }

            var fullText = string.Concat(streamedText);

            // The model signals work to do via structured Ollama tool_calls on its message.
            var pendingToolCalls = ParseToolCalls(toolCallsNode);

            if (pendingToolCalls.Count == 0 || toolCallsNode is null)
            {
                // No tool calls — this is the final answer.
                finalText = fullText;
                break;
            }

            // Record the assistant's tool-call message in history, replaying the model's
            // own tool_calls array verbatim so the next request matches Ollama's format.
            history.Add(new AgentMessage("assistant", fullText, ToolCalls: toolCallsNode));

            // Execute each tool call.
            foreach (var tc in pendingToolCalls)
            {
                events.ToolCallStart?.Invoke(tc);

                // Permission gate based on mode.
                var approved = mode switch
                {
                    AgentMode.Bypass => true,
                    AgentMode.AutoPilot => true,
                    AgentMode.Interactive => await (events.RequestPermission?.Invoke(tc) ?? Task.FromResult(true)),
                    _ => true
                };

                string? toolResult;
                if (!approved)
                {
                    toolResult = "Permission denied by user.";
                }
                else
                {
                    toolResult = await ExecuteToolAsync(tc, ct);
                    toolCallsExecuted++;
                }

                events.ToolCallComplete?.Invoke(tc, approved, toolResult);

                // Ollama expects tool output as a role:"tool" message correlated by tool_call_id.
                history.Add(new AgentMessage("tool", toolResult ?? string.Empty, ToolCallId: tc.Id));
            }
        }

        // AutoPilot: if the model's final text signals "not done" and we have budget, continue.
        if (mode == AgentMode.AutoPilot && finalText is not null && LooksIncomplete(finalText) && autopilotBudget > 0)
        {
            autopilotBudget--;
            autoPilotContinued = true;
            events.AutoPilotContinuing?.Invoke(autopilotBudget);

            // Recurse with a continuation prompt.
            var continuation = await RunTurnAsync(upstream, agent, model, mode, history,
                "Continue. You were not finished. Complete the remaining work.", events, ct);
            return new AgentTurnResult(continuation.FinalText, continuation.Completed,
                toolCallsExecuted + continuation.ToolCallsExecuted, true);
        }

        var result = new AgentTurnResult(finalText ?? string.Empty, finalText is not null, toolCallsExecuted, autoPilotContinued);
        events.TurnComplete?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Builds the JSON payload for the proxy's /api/chat endpoint.
    /// </summary>
    private async Task<object> BuildChatPayload(AgentConfig agent, string model, List<AgentMessage> history, AgentMode mode, double temperature)
    {
        var messages = new List<JsonObject>();

        // Load and inject instruction files into system context
        var instructionContent = await LoadInstructionFilesAsync();
        if (!string.IsNullOrWhiteSpace(instructionContent))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructionContent });
        }

        // System prompt first.
        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = agent.SystemPrompt });

        // Conversation history.
        foreach (var m in history)
        {
            var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCalls is not null) msg["tool_calls"] = m.ToolCalls.DeepClone();
            if (m.ToolCallId is not null) msg["tool_call_id"] = m.ToolCallId;
            messages.Add(msg);
        }

        var messagesArray = new JsonArray();
        foreach (var m in messages) messagesArray.Add(m);

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messagesArray,
            ["stream"] = true,
            ["options"] = new JsonObject { ["temperature"] = temperature }
        };

        // Tools are sent for any agent that has tool access. Tools == null means "all tools"
        // (Agent/Plan); an explicit empty array means none (Ask).
        if (agent.Tools is null || agent.Tools.Count > 0)
        {
            var tools = _mcp.GetAvailableToolDefinitions(agent.Tools);
            if (tools.Count > 0)
            {
                var toolsArray = new JsonArray();
                foreach (var t in tools) toolsArray.Add(t);
                payload["tools"] = toolsArray;
            }
        }

        return payload;
    }

    /// <summary>
    /// Executes a tool call by routing to the appropriate MCP server.
    /// </summary>
    private Task<string> ExecuteToolAsync(ToolCallRequest tc, CancellationToken ct)
    {
        // Route by tool name prefix: "<server-key>-<tool-name>" or just "<tool-name>".
        return _mcp.ExecuteToolAsync(tc.Name, tc.Arguments?.ToJsonString(), ct);
    }

    /// <summary>
    /// Heuristic: does the model's final text look like it's not yet done?
    /// AutoPilot uses this to decide whether to auto-continue.
    /// </summary>
    private static bool LooksIncomplete(string text)
    {
        // Simple heuristic: the model explicitly signals continuation intent.
        var t = text.Trim().ToLowerInvariant();
        return t.Contains("not done") || t.Contains("incomplete") || t.Contains("continue with") || t.Contains("next step");
    }

    /// <summary>
    /// Converts the Ollama <c>message.tool_calls</c> array
    /// (<c>[{ "id", "function": { "name", "arguments" } }]</c>) into runtime tool-call requests.
    /// </summary>
    private static List<ToolCallRequest> ParseToolCalls(JsonNode? toolCallsNode)
    {
        var results = new List<ToolCallRequest>();
        if (toolCallsNode is not JsonArray array) return results;

        foreach (var item in array)
        {
            if (item is not JsonObject call) continue;
            if (call["function"] is not JsonObject fn) continue;

            var name = fn["name"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            // Ollama may deliver arguments as a nested object or as a JSON-encoded string.
            JsonNode? args = fn["arguments"];
            if (args is JsonValue value && value.TryGetValue<string>(out var argText))
            {
                try { args = JsonNode.Parse(argText); }
                catch { args = null; }
            }

            var id = call["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString("N");

            results.Add(new ToolCallRequest(id!, name, args));
        }

        return results;
    }

    /// <summary>
    /// Builds the instruction system-prompt content from the Settings → Instructions list, the single
    /// source of truth (inline text entries plus files read from disk). When nothing has been
    /// configured yet, the two defaults are used so the common case keeps working.
    /// </summary>
    private async Task<string> LoadInstructionFilesAsync()
    {
        try
        {
            var solutionRoot = InstructionFileLoader.GetSolutionRootPath(); var settings = await _settings.LoadAsync().ConfigureAwait(false);
            var entries = settings.Instructions;
            if (entries is null || entries.Length == 0)
                entries = InstructionFileLoader.DefaultEntries().ToArray();
            var instructions = await Task.Run(() => { InstructionFileLoader.EnsureDefaultFiles(entries, solutionRoot); return InstructionFileLoader.LoadFromSettings(entries, solutionRoot); }).ConfigureAwait(false);
            return InstructionFileLoader.CombineInstructions(instructions);
        }
        catch
        {
            return string.Empty;
        }
    }
}
