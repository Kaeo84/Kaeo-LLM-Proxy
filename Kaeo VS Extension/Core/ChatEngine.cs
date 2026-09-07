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
    private readonly AgentRuntime _runtime;

    public ChatEngine(AgentRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Runs a full agent turn (Ask/Agent/Plan) with streaming events against the given Ollama connection.
    /// Ask mode = no tools (plain chat); Agent/Plan = tool loop via the proxy.
    /// </summary>
    public Task<AgentTurnResult> RunAsync(
        OllamaApiClient ollama,
        AgentConfig agent,
        string model,
        AgentMode mode,
        List<AgentMessage> history,
        string prompt,
        AgentEvents events,
        CancellationToken ct = default)
    {
        return _runtime.RunTurnAsync(ollama, agent, model, mode, history, prompt, events, ct);
    }
}
