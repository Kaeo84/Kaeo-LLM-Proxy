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
    /// <summary>Fires at the top of each tool-loop iteration after the first, so the UI
    /// can reset streaming state (e.g. clear leading-whitespace tracking for the new batch).</summary>
    public Action? IterationStarting;
    /// <summary>The turn has completed (final message or error).</summary>
    public Action<AgentTurnResult>? TurnComplete;
    /// <summary>An error occurred.</summary>
    public Action<Exception>? Error;
    /// <summary>Checkpoint status notice (auto-checkpoint at turn start).</summary>
    public Action<string>? CheckpointNotice;
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
    private const int MaxToolIterations = 50;

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

        // Mode-aware built-in tools (e.g. vs_inquiry's AutoPilot-only timeout) read this.
        BuiltInVsTools.CurrentMode = mode;

        history.Add(new ChatMessage(ChatRole.User, userPrompt));
        var defaults = (await _settings.LoadAsync()).Defaults ?? new Defaults();
        var maxIterations = defaults.MaxToolIterations > 0 ? defaults.MaxToolIterations : MaxToolIterations;
        var toolCallsExecuted = 0;
        var autoPilotContinued = false;
        var autopilotBudget = defaults.AutoPilotBudget > 0 ? defaults.AutoPilotBudget : DefaultAutoPilotBudget;

        // Instruction files + settings are read ONCE per turn, not per tool-loop iteration
        // (the old per-iteration load re-read/decrypted settings and probed the disk up to
        // 50× per turn and resurrected deliberately deleted default files).
        var instructionContent = await LoadInstructionFilesAsync();

        // MCP tools are projected as AIFunctions so the request payload carries their
        // schemas; execution stays manual (not middleware-driven) so each call passes the
        // mode's permission gate and raises the tool events the UI renders.
        List<AITool> tools = await BuildToolsAsync(agent);

        // Snapshot the worktree before a tool-enabled turn so bad edits can be rolled back
        // (vs_checkpoint_rollback). Failures degrade to an honest notice; the turn proceeds.
        if (defaults.AutoCheckpoint && tools.Count > 0)
        {
            var notice = await VsCheckpointTools.TryAutoCheckpointAsync(ct);
            events.CheckpointNotice?.Invoke(notice);
        }

        string? finalText;
        var doomLoop = new DoomLoopDetector(defaults.DoomLoopThreshold);
        var doomLoopTripped = false;

        // AutoPilot continuations run as an outer loop rather than recursion so the budget
        // is tracked across the WHOLE run (recursing re-read the defaults and reset it,
        // making "not done" loops unbounded). TurnComplete fires exactly once, at the end.
        while (true)
        {
            finalText = null;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                // Reset whitespace tracking at the start of each new model call so
                // leading blank deltas from the second+ iteration are also stripped.
                if (iteration > 0)
                    events.IterationStarting?.Invoke();

                // Steering: prompts the user submitted mid-run enter the conversation at a
                // clean iteration boundary, ahead of building the next request.
                if (events.DrainRedirects is { } drainRedirects)
                {
                    foreach (var redirect in drainRedirects())
                        history.Add(new ChatMessage(ChatRole.User, redirect));
                }

                var messages = BuildRequestMessages(agent, history, instructionContent);

                var options = new ChatOptions
                {
                    ModelId = model,
                    Temperature = (float)defaults.DefaultTemperature,
                    Tools = tools.Count > 0 ? tools : null,
                };

                var textParts = new List<string>();
                var reasoningParts = new List<string>();
                var functionCalls = new List<FunctionCallContent>();

                try
                {
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
                }
                catch (Exception)
                {
                    // L8: keep whatever assistant text streamed before the failure so the history
                    // reflects the partial turn instead of silently dropping it, then rethrow.
                    var partialText = string.Concat(textParts);
                    if (partialText.Length > 0)
                        history.Add(new ChatMessage(ChatRole.Assistant, partialText + "\n\n[response truncated: stream failed]"));
                    throw;
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

                    // Permission gate based on mode. A missing Interactive handler is a wiring
                    // bug, not consent: deny rather than silently degrading to Bypass.
                    var approved = mode switch
                    {
                        AgentMode.Bypass => true,
                        AgentMode.AutoPilot => true,
                        AgentMode.Interactive => await InteractiveApprovalAsync(tc, defaults, events),
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

                        // Optional context guard: cap oversized tool results before they enter
                        // history (verbose build logs etc. can otherwise consume the window).
                        if (defaults.CapToolResults && toolResult is not null)
                        {
                            var cap = defaults.ToolResultMaxChars > 0 ? defaults.ToolResultMaxChars : 30_000;
                            if (toolResult.Length > cap)
                                toolResult = toolResult.Substring(0, cap)
                                    + $"\n…[result truncated at {cap} chars (CapToolResults); original {toolResult.Length} chars]";
                        }
                    }

                    events.ToolCallComplete?.Invoke(tc, approved, toolResult);

                    // Ollama expects tool output as a role:"tool" message correlated by tool_call_id.
                    history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(tc.Id, toolResult ?? string.Empty)]));

                    // Doom-loop guard: identical call failing N times in a row stops the turn
                    // with an honest notice instead of burning the iteration budget.
                    if (defaults.DoomLoopGuard && approved
                        && doomLoop.Record(tc.Name, tc.Arguments?.ToJsonString(), DoomLoopDetector.LooksLikeFailure(toolResult)))
                    {
                        var notice = $"[doom-loop guard: '{tc.Name}' failed {Math.Max(2, defaults.DoomLoopThreshold)} times in a row with identical arguments — turn stopped. Try a different approach or ask the user.]";
                        history.Add(new ChatMessage(ChatRole.User, notice));
                        finalText = notice;
                        doomLoopTripped = true;
                        break;
                    }
                }

                if (doomLoopTripped) break;
            }

            // AutoPilot: if the model's final text signals "not done" and we have budget,
            // append a continuation prompt and run another pass against the shared budget.
            if (mode == AgentMode.AutoPilot && !doomLoopTripped && finalText is not null && LooksIncomplete(finalText) && autopilotBudget > 0)
            {
                autopilotBudget--;
                autoPilotContinued = true;
                events.AutoPilotContinuing?.Invoke(autopilotBudget);
                history.Add(new ChatMessage(ChatRole.User, "Continue. You were not finished. Complete the remaining work."));
                continue;
            }

            break;
        }

        var result = new AgentTurnResult(finalText ?? string.Empty, finalText is not null, toolCallsExecuted, autoPilotContinued);
        events.TurnComplete?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Interactive-mode approval: known read-only tools auto-approve when the user enabled
    /// AutoApproveReadOnly; everything else goes through the permission card. A missing
    /// handler is a wiring bug, not consent: deny rather than silently degrading to Bypass.
    /// </summary>
    private static async Task<bool> InteractiveApprovalAsync(ToolCallRequest tc, Defaults defaults, AgentEvents events)
    {
        if (defaults.AutoApproveReadOnly && ToolRisk.IsReadOnly(tc.Name))
            return true;
        return await (events.RequestPermission?.Invoke(tc) ?? Task.FromResult(false));
    }

    /// <summary>
    /// Builds the request message list: ONE leading system message (instruction files merged
    /// with the agent's system prompt) followed by the conversation history. llama.cpp's strict
    /// chat templates (e.g. Gemma-style) allow a single system message at index 0 and raise
    /// "System message must be at the beginning." otherwise, so the parts must be merged
    /// rather than sent as separate system messages.
    /// </summary>
    private static List<ChatMessage> BuildRequestMessages(AgentConfig agent, List<ChatMessage> history, string instructionContent)
    {
        var messages = new List<ChatMessage>();

        var systemParts = new List<string>();

        // Inject the per-turn instruction snapshot into system context.
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
        // Anchor to the FINAL sentence: ordinary prose merely mentioning "next step" or
        // "incomplete" mid-answer ("The next step is to run tests.") must not trigger an
        // AutoPilot continuation — only a closing statement that signals unfinished work.
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        var lastSentence = trimmed
            .Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim()
            .ToLowerInvariant();
        if (lastSentence is null || lastSentence.Length == 0)
            return false;

        return lastSentence.Contains("not done") || lastSentence.Contains("incomplete")
            || lastSentence.Contains("continue with") || lastSentence.Contains("next step");
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
            var solutionRoot = await InstructionFileLoader.GetSolutionRootPathAsync();
            var settings = await _settings.LoadAsync().ConfigureAwait(false);
            var entries = settings.Instructions;
            if (entries is null || entries.Length == 0)
                entries = [.. InstructionFileLoader.DefaultEntries()];
            // Default files are seeded at package/settings init, not here: recreating them
            // per turn resurrected files the user deliberately deleted.
            var detectConventions = settings.Defaults?.DetectConventionFiles ?? true;
            var instructions = await Task.Run(() =>
            {
                var loaded = InstructionFileLoader.LoadFromSettings(entries, solutionRoot);
                if (detectConventions)
                    loaded.AddRange(InstructionFileLoader.LoadConventionFiles(solutionRoot, loaded));
                return loaded;
            }).ConfigureAwait(false);
            var combined = InstructionFileLoader.CombineInstructions(instructions);

            // Task memory: surface recent agent notes so decisions survive compaction and new
            // sessions. Read on this background thread; tolerant of a missing/locked notes file.
            var notes = VsNotesTools.ReadRecentNotes(solutionRoot, 10);
            if (!string.IsNullOrWhiteSpace(notes))
                combined += "\n\n## Recent session notes (from .kaeo/notes.md, newest first)\n" + notes;

            return combined;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Failed to load instruction files; continuing without them: {ex.Message}");
            return string.Empty;
        }
    }
}
