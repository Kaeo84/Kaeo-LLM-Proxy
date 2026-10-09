using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

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
    /// <summary>
    /// Pops user prompts queued as redirects while the turn was running. Called at each
    /// tool-loop iteration boundary so mid-run input lands as a fresh user message ahead
    /// of the next model call; an empty list is the norm.
    /// </summary>
    public Func<IReadOnlyList<string>>? DrainRedirects;
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
/// on top of the Microsoft.Extensions.AI chat model, running against the Kaeo LLM
/// Proxy through an IChatClient. Reasoning arrives as TextReasoningContent updates
/// (raised via <see cref="AgentEvents.ReasoningDelta"/>), tool calls as
/// FunctionCallContent, and tool results are appended as FunctionResultContent.
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
    /// Runs a single agent turn against the given chat client: streams the model response
    /// (text and reasoning deltas), executes any requested tool calls behind the mode's
    /// permission gate, feeds results back, and repeats until the model produces a final
    /// answer or the iteration budget is exhausted. Emits events via <paramref name="events"/>.
    /// </summary>
    public async Task<AgentTurnResult> RunTurnAsync(
        IChatClient client,
        AgentConfig agent,
        string model,
        AgentMode mode,
        List<ChatMessage> history,
        string userPrompt,
        AgentEvents events,
        CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (events is null) throw new ArgumentNullException(nameof(events));

        history.Add(new ChatMessage(ChatRole.User, userPrompt));
        var defaults = (await _settings.LoadAsync()).Defaults ?? new Defaults();
        var maxIterations = defaults.MaxToolIterations > 0 ? defaults.MaxToolIterations : MaxToolIterations;
        var toolCallsExecuted = 0;
        var autoPilotContinued = false;
        var autopilotBudget = DefaultAutoPilotBudget;

        // MCP tools are projected as AIFunctions so the request payload carries their
        // schemas; execution stays manual (not middleware-driven) so each call passes the
        // mode's permission gate and raises the tool events the UI renders.
        List<AITool> tools = await BuildToolsAsync(agent);

        string? finalText = null;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // Steering: prompts the user submitted mid-run enter the conversation at a
            // clean iteration boundary, ahead of building the next request.
            if (events.DrainRedirects is { } drainRedirects)
            {
                foreach (var redirect in drainRedirects())
                    history.Add(new ChatMessage(ChatRole.User, redirect));
            }

            var messages = await BuildRequestMessagesAsync(agent, history);

            var options = new ChatOptions
            {
                ModelId = model,
                Temperature = (float)defaults.DefaultTemperature,
                Tools = tools.Count > 0 ? tools : null,
            };

            var textParts = new List<string>();
            var reasoningParts = new List<string>();
            var functionCalls = new List<FunctionCallContent>();

            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            textParts.Add(text.Text);
                            events.TextDelta?.Invoke(text.Text);
                            break;

                        case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                            reasoningParts.Add(reasoning.Text);
                            events.ReasoningDelta?.Invoke(reasoning.Text);
                            break;

                        case FunctionCallContent call:
                            functionCalls.Add(call);
                            break;
                    }
                }
            }

            var fullText = string.Concat(textParts);
            var fullReasoning = string.Concat(reasoningParts);

            if (functionCalls.Count == 0)
            {
                // No tool calls — this is the final answer.
                finalText = fullText;
                break;
            }

            // Record the assistant's tool-call message in history; the adapter regenerates
            // the Ollama tool_calls array (preserving ids) on the next request.
            List<AIContent> assistantContents = [];
            if (fullReasoning.Length > 0)
                assistantContents.Add(new TextReasoningContent(fullReasoning));
            if (fullText.Length > 0)
                assistantContents.Add(new TextContent(fullText));
            assistantContents.AddRange(functionCalls);
            history.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

            // Execute each tool call behind the permission gate.
            foreach (FunctionCallContent call in functionCalls)
            {
                var tc = new ToolCallRequest(
                    string.IsNullOrEmpty(call.CallId) ? Guid.NewGuid().ToString("N") : call.CallId!,
                    call.Name,
                    ToArgumentsNode(call.Arguments));

                events.ToolCallStart?.Invoke(tc);

                // Permission gate based on mode.
                var approved = mode switch
                {
                    AgentMode.Bypass => true,
                    AgentMode.AutoPilot => true,
                    AgentMode.Interactive => await (events.RequestPermission?.Invoke(tc) ?? Task.FromResult(true)),
                    _ => true
                };

                // Stop pressed while a card was open resolves the handshake as denied; unwind
                // here (before executing) instead of feeding a dead tool result to the model.
                ct.ThrowIfCancellationRequested();

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
                history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(tc.Id, toolResult ?? string.Empty)]));
            }
        }

        // AutoPilot: if the model's final text signals "not done" and we have budget, continue.
        if (mode == AgentMode.AutoPilot && finalText is not null && LooksIncomplete(finalText) && autopilotBudget > 0)
        {
            autopilotBudget--;
            autoPilotContinued = true;
            events.AutoPilotContinuing?.Invoke(autopilotBudget);

            // Recurse with a continuation prompt.
            var continuation = await RunTurnAsync(client, agent, model, mode, history,
                "Continue. You were not finished. Complete the remaining work.", events, ct);
            return new AgentTurnResult(continuation.FinalText, continuation.Completed,
                toolCallsExecuted + continuation.ToolCallsExecuted, true);
        }

        var result = new AgentTurnResult(finalText ?? string.Empty, finalText is not null, toolCallsExecuted, autoPilotContinued);
        events.TurnComplete?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Builds the request message list: ONE leading system message (instruction files merged
    /// with the agent's system prompt) followed by the conversation history. llama.cpp's strict
    /// chat templates (e.g. Gemma-style) allow a single system message at index 0 and raise
    /// "System message must be at the beginning." otherwise, so the parts must be merged
    /// rather than sent as separate system messages.
    /// </summary>
    private async Task<List<ChatMessage>> BuildRequestMessagesAsync(AgentConfig agent, List<ChatMessage> history)
    {
        var messages = new List<ChatMessage>();

        var systemParts = new List<string>();

        // Load and inject instruction files into system context
        var instructionContent = await LoadInstructionFilesAsync();
        if (!string.IsNullOrWhiteSpace(instructionContent))
        {
            systemParts.Add(instructionContent);
        }

        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            systemParts.Add(agent.SystemPrompt);

        if (systemParts.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, string.Join("\n\n", systemParts)));

        messages.AddRange(history);
        return messages;
    }

    /// <summary>
    /// Projects the agent's enabled MCP tools as AIFunctions for the request payload.
    /// Tools == null means "all tools" (Agent/Plan); an explicit empty array means none (Ask).
    /// </summary>
    private Task<List<AITool>> BuildToolsAsync(AgentConfig agent)
    {
        if (agent.Tools is not null && agent.Tools.Count == 0)
            return Task.FromResult<List<AITool>>([]);

        var definitions = _mcp.GetAvailableToolDefinitions(agent.Tools);
        List<AITool> tools = [.. definitions.Select(definition => new McpToolAIFunction(definition, _mcp))];
        return Task.FromResult(tools);
    }

    /// <summary>
    /// Converts MEAI function-call arguments to the JsonNode shape the runtime and
    /// event consumers carry.
    /// </summary>
    private static JsonNode? ToArgumentsNode(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return new JsonObject();

        return JsonSerializer.SerializeToNode(new Dictionary<string, object?>(arguments)) ?? new JsonObject();
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
