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
/// A single message in the conversation history.
/// </summary>
internal sealed record AgentMessage(string Role, string Content, JsonNode? ToolCall = null, JsonNode? ToolResult = null);

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

    /// <summary>Maximum tool-call iterations per turn (prevents infinite tool loops).</summary>
    private const int MaxToolIterations = 10;

    /// <summary>Default AutoPilot continuation budget.</summary>
    private const int DefaultAutoPilotBudget = 5;

    public AgentRuntime(McpServerManager mcp)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    /// <summary>
    /// Runs a single agent turn against the given Ollama connection: streams the model response,
    /// executes any requested tool calls, feeds results back, and repeats until the model produces
    /// a final answer or the iteration budget is exhausted. Emits events via <paramref name="events"/>.
    /// </summary>
    public async Task<AgentTurnResult> RunTurnAsync(
        OllamaApiClient ollama,
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
        var toolCallsExecuted = 0;
        var autoPilotContinued = false;
        var autopilotBudget = DefaultAutoPilotBudget;

        string? finalText = null;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            // Build the request payload for the proxy's /api/chat.
            var payload = BuildChatPayload(agent, model, history, mode);

            var streamedText = new List<string>();
            var pendingToolCalls = new List<ToolCallRequest>();

            // Stream the model response.
            await foreach (var chunk in ollama.StreamChatAsync(payload, ct))
            {
                if (chunk.Text is not null)
                {
                    streamedText.Add(chunk.Text);
                    events.TextDelta?.Invoke(chunk.Text);
                }

                if (chunk.Done)
                {
                    // The final chunk in Ollama NDJSON may carry tool_calls in the message.
                    // We parse the accumulated text for tool-call markers as a fallback.
                    break;
                }
            }

            var fullText = string.Concat(streamedText);

            // Check for tool calls in the response.
            pendingToolCalls = ParseToolCalls(fullText);

            if (pendingToolCalls.Count == 0)
            {
                // No tool calls — this is the final answer.
                finalText = fullText;
                break;
            }

            // Record the assistant's tool-call message in history.
            // JsonNode.Parse(JsonSerializer.Serialize(...)) is the net48-compatible
            // equivalent of JsonSerializer.SerializeToNode (net7+).
            history.Add(new AgentMessage("assistant", fullText, ToolCall: JsonNode.Parse(JsonSerializer.Serialize(pendingToolCalls))));

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
                history.Add(new AgentMessage("tool", toolResult ?? string.Empty, ToolResult: tc.Arguments));
            }
        }

        // AutoPilot: if the model's final text signals "not done" and we have budget, continue.
        if (mode == AgentMode.AutoPilot && finalText is not null && LooksIncomplete(finalText) && autopilotBudget > 0)
        {
            autopilotBudget--;
            autoPilotContinued = true;
            events.AutoPilotContinuing?.Invoke(autopilotBudget);

            // Recurse with a continuation prompt.
            var continuation = await RunTurnAsync(ollama, agent, model, mode, history,
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
    private object BuildChatPayload(AgentConfig agent, string model, List<AgentMessage> history, AgentMode mode)
    {
        var messages = new List<JsonObject>();

        // System prompt first.
        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = agent.SystemPrompt });

        // Conversation history.
        foreach (var m in history)
        {
            var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCall is not null) msg["tool_calls"] = m.ToolCall;
            if (m.ToolResult is not null) msg["tool_result"] = m.ToolResult;
            messages.Add(msg);
        }

        // Filter tools by agent config if specified.
        var availableTools = agent.Tools is null ? null : agent.Tools;

        var messagesArray = new JsonArray();
        foreach (var m in messages) messagesArray.Add(m);

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messagesArray,
            ["stream"] = true,
            ["options"] = new JsonObject { ["temperature"] = 0.2 }
        };

        // Tools are only sent when the agent has tool access (Agent/Plan), not plain Ask.
        if (agent.Tools is not null)
        {
            var tools = _mcp.GetAvailableToolDefinitions(availableTools);
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
    /// Parses tool-call markers from the model's response text.
    /// The proxy's Ollama format returns tool calls as structured JSON in the message;
    /// this is a fallback text-based parser for models that emit tool calls inline.
    /// </summary>
    private static List<ToolCallRequest> ParseToolCalls(string text)
    {
        var results = new List<ToolCallRequest>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        // Try to find JSON tool-call blocks in the text.
        // Convention: {"tool_call": {"name": "...", "arguments": {...}}}
        var idx = 0;
        while ((idx = text.IndexOf("\"tool_call\"", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            try
            {
                // Find the enclosing JSON object.
                var start = text.LastIndexOf('{', idx);
                var depth = 0;
                var end = start;
                for (var i = start; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}') { depth--; if (depth == 0) { end = i + 1; break; } }
                }
                if (end > start)
                {
                    var obj = JsonNode.Parse(text[start..end]);
                    if (obj is JsonObject o && o["tool_call"] is JsonObject tc)
                    {
                        var name = tc["name"]?.GetValue<string>() ?? string.Empty;
                        var args = tc["arguments"];
                        var id = tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                        if (!string.IsNullOrEmpty(name))
                            results.Add(new ToolCallRequest(id, name, args));
                    }
                }
            }
            catch
            {
                // Malformed JSON — skip.
            }
            idx++;
        }

        return results;
    }
}
