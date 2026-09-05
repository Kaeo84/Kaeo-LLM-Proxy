using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// High-level facade the UI calls into. Wraps the AgentRuntime (for Agent/Plan modes with tools)
/// and the OllamaApiClient (for Ask mode — plain chat, no tools).
/// </summary>
internal sealed class ChatEngine
{
    private readonly OllamaApiClient _ollama;
    private readonly AgentRuntime _runtime;
    private readonly ExtensionSettingsStore _settingsStore;

    public ChatEngine(OllamaApiClient ollama, AgentRuntime runtime, ExtensionSettingsStore settingsStore)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    /// <summary>
    /// Runs a full agent turn (Ask/Agent/Plan) with streaming events.
    /// Ask mode = no tools (plain chat); Agent/Plan = tool loop via the proxy.
    /// </summary>
    public Task<AgentTurnResult> RunAsync(
        AgentConfig agent,
        string model,
        AgentMode mode,
        List<AgentMessage> history,
        string prompt,
        AgentEvents events,
        CancellationToken ct = default)
    {
        return _runtime.RunTurnAsync(agent, model, mode, history, prompt, events, ct);
    }
}
