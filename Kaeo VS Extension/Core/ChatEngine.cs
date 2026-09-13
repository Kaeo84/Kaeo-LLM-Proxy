using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// High-level facade the UI calls into. Wraps the AgentRuntime (for Agent/Plan modes with tools)
/// and the OllamaChatClient (for Ask mode — plain chat, no tools).
/// </summary>
internal sealed class ChatEngine
{
    private readonly AgentRuntime _runtime;

    public ChatEngine(AgentRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Runs a full agent turn (Ask/Agent/Plan) with streaming events against the given upstream.
    /// Ask mode = no tools (plain chat); Agent/Plan = tool loop via the proxy.
    /// </summary>
    public Task<AgentTurnResult> RunAsync(
        IChatClient client,
        AgentConfig agent,
        string model,
        AgentMode mode,
        List<ChatMessage> history,
        string prompt,
        AgentEvents events,
        CancellationToken ct = default)
    {
        return _runtime.RunTurnAsync(client, agent, model, mode, history, prompt, events, ct);
    }
}
