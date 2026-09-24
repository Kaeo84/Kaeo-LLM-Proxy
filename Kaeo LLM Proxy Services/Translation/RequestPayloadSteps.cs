using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Resolves the effective and resolved model names and records the /compact redirect decision.
/// Runs first because every later step depends on which model's mapping governs the request.
/// </summary>
internal sealed class ResolveModelStep : IRequestPayloadStep
{
    public string Name => "resolve-model";

    public bool Applies(RequestPayloadContext context) => true;

    public void Apply(RequestPayloadContext context)
    {
        string original = RequestPayloadReads.StringMember(context.Payload, "model") ?? string.Empty;
        string? firstContent = RequestPayloadReads.FirstMessageContent(context.Payload);

        string effectiveModel = OllamaProxyHandler.ResolveEffectiveModel(context.Settings, original, firstContent);
        bool compactRedirected = !string.Equals(effectiveModel, original, StringComparison.OrdinalIgnoreCase);

        context.OriginalModel = original;
        context.EffectiveModel = effectiveModel;
        context.ResolvedModel = context.Settings.ResolveModelName(effectiveModel);
        context.CompactRedirected = compactRedirected;
        context.Mapping = context.Settings.FindModelMapping(effectiveModel);
        context.ApplyThinkingCompatibility =
            OllamaProxyHandler.ShouldApplyThinkingCompatibility(context.Settings, effectiveModel);
        context.InjectedInstructions = OllamaProxyHandler.GetInstructionTextForModel(context.Settings, effectiveModel);

        context.Log.Model = effectiveModel;

        if (compactRedirected)
        {
            context.Log.OriginalModel = original;
            Log.Debug(
                "Context-summarize (/compact) request for {OriginalModel} redirected to compaction model {CompactModel}",
                original, effectiveModel);
        }
        else if (OllamaProxyHandler.IsContextSummarizeRequest(firstContent))
        {
            // Only explain a non-redirect when the request actually WAS a /compact request. Logging
            // this for every ordinary chat request claimed a redirect had been "not applied" to
            // requests that never asked for compaction.
            Log.Debug(
                "Context-summarize (/compact) request for {OriginalModel} is not redirected: {Reason}",
                original, OllamaProxyHandler.DescribeCompactSkipReason(context.Settings, original, firstContent));
        }
    }
}

/// <summary>
/// Rewrites the <c>model</c> member to the upstream model name the mapping resolves to.
/// </summary>
internal sealed class RewriteModelNameStep : IRequestPayloadStep
{
    public string Name => "rewrite-model-name";

    public bool Applies(RequestPayloadContext context)
        => !string.Equals(context.OriginalModel, context.ResolvedModel, StringComparison.Ordinal);

    public void Apply(RequestPayloadContext context)
    {
        foreach (string key in context.Payload.Select(pair => pair.Key).ToList())
        {
            if (key.Equals("model", StringComparison.OrdinalIgnoreCase))
                context.Payload[key] = context.ResolvedModel;
        }

        context.Rewritten = true;
    }
}

/// <summary>
/// Applies the mapping's sampling priorities to <c>temperature</c> and <c>repeat_penalty</c>:
/// <c>Provider</c> drops the client's value so the platform default survives, <c>Proxy</c>
/// overwrites or injects the configured value, and <c>ClientApp</c> leaves it alone.
/// </summary>
internal sealed class SamplingPriorityStep : IRequestPayloadStep
{
    public string Name => "sampling-priority";

    public bool Applies(RequestPayloadContext context)
    {
        context.TemperaturePriority = context.Mapping?.TemperaturePriority ?? SamplingPriority.ClientApp;
        context.RepeatPenaltyPriority = context.Mapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp;
        context.ProxyTemperature = context.Mapping?.Temperature ?? 0.7;
        context.ProxyRepeatPenalty = context.Mapping?.RepeatPenalty ?? 1.0;

        // Captured before mutating so the debug summary can show what the client actually sent.
        context.ClientTemperature = ReadNumber(context.Payload, "temperature");
        context.ClientRepeatPenalty = ReadNumber(context.Payload, "repeat_penalty");

        return context.TemperaturePriority != SamplingPriority.ClientApp
            || context.RepeatPenaltyPriority != SamplingPriority.ClientApp;
    }

    public void Apply(RequestPayloadContext context)
    {
        ApplyMember(context, "temperature", context.TemperaturePriority, context.ProxyTemperature);
        ApplyMember(context, "repeat_penalty", context.RepeatPenaltyPriority, context.ProxyRepeatPenalty);
        context.Rewritten = true;
    }

    /// <summary>Reads a numeric member as a float, matching the legacy number-or-numeric-string rule.</summary>
    private static float? ReadNumber(JsonObject payload, string name)
    {
        foreach ((string key, JsonNode? value) in payload)
        {
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return value switch
            {
                JsonValue jsonValue when jsonValue.TryGetValue(out float number) => number,
                JsonValue jsonValue when jsonValue.TryGetValue(out double wide) => (float)wide,
                _ => null,
            };
        }

        return null;
    }

    private static void ApplyMember(RequestPayloadContext context, string memberName, SamplingPriority priority, double value)
    {
        // ClientApp leaves the member exactly as the client sent it. Returning early matters: the
        // step applies when *either* sampling priority is non-default, so the other member must not
        // be touched - injecting a default here would add a value the client never chose.
        if (priority == SamplingPriority.ClientApp)
            return;

        string? existingKey = context.Payload
            .Select(pair => pair.Key)
            .FirstOrDefault(key => key.Equals(memberName, StringComparison.OrdinalIgnoreCase));

        if (priority == SamplingPriority.Provider)
        {
            if (existingKey is not null)
                context.Payload.Remove(existingKey);
            return;
        }

        // Proxy priority overwrites the client's value and injects the configured one when absent.
        context.Payload[existingKey ?? memberName] = value;
    }
}

/// <summary>
/// Applies the mapping's reasoning-effort priority in every wire shape the mapping selected:
/// legacy <c>reasoning_effort</c>, modern <c>reasoning</c> object, the Qwen Cloud <c>extra_body</c>
/// wrapper, and <c>chat_template_kwargs</c> for local inference servers.
/// </summary>
internal sealed class ReasoningEffortStep : IRequestPayloadStep
{
    public string Name => "reasoning-effort";

    public bool Applies(RequestPayloadContext context)
    {
        context.ReasoningEffortPriority = context.Mapping?.ReasoningEffortPriority ?? SamplingPriority.ClientApp;
        // Providers expect lowercase effort levels (e.g. OpenAI rejects "High").
        context.ProxyReasoningEffort = context.Mapping?.ReasoningEffort?.Trim().ToLowerInvariant() ?? string.Empty;
        context.ReasoningEffortFormat = context.Mapping?.ReasoningEffortFormat ?? ReasoningEffortFormat.Legacy;
        context.ClientReasoningEffort = ReadEffort(context.Payload);

        bool injecting = context.ReasoningEffortPriority == SamplingPriority.Proxy
            && context.ProxyReasoningEffort.Length > 0;
        return context.ReasoningEffortPriority == SamplingPriority.Provider || injecting;
    }

    public void Apply(RequestPayloadContext context)
    {
        bool injecting = context.ReasoningEffortPriority == SamplingPriority.Proxy
            && context.ProxyReasoningEffort.Length > 0;

        RemoveMember(context, "reasoning_effort");
        RemoveMember(context, "reasoning");
        RemoveMember(context, "extra_body");
        RemoveMember(context, "chat_template_kwargs");

        if (injecting)
        {
            ReasoningEffortFormat format = context.ReasoningEffortFormat;
            string effort = context.ProxyReasoningEffort;

            if (format.HasFlag(ReasoningEffortFormat.Legacy))
                context.Payload["reasoning_effort"] = effort;

            if (format.HasFlag(ReasoningEffortFormat.Modern))
                context.Payload["reasoning"] = new JsonObject { ["enable"] = true, ["thinking_level"] = effort };

            if (format.HasFlag(ReasoningEffortFormat.QwenCloud))
                context.Payload["extra_body"] = new JsonObject { ["enable_thinking"] = true, ["reasoning_effort"] = effort };

            if (format.HasFlag(ReasoningEffortFormat.ChatTemplateKwargs))
                context.Payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true, ["reasoning_effort"] = effort };
        }

        context.Rewritten = true;
    }

    private static string? ReadEffort(JsonObject payload)
        => RequestPayloadReads.StringMember(payload, "reasoning_effort");

    private static void RemoveMember(RequestPayloadContext context, string name)
    {
        string? key = context.Payload
            .Select(pair => pair.Key)
            .FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (key is not null)
            context.Payload.Remove(key);
    }
}

/// <summary>
/// Drops the <c>stream_options</c> block when Copilot compatibility applies, reporting what the
/// client asked for through <see cref="RequestPayloadContext.StreamOptions"/> so the response path
/// can synthesize the terminal usage chunk many servers never deliver.
/// </summary>
internal sealed class StreamOptionsStep : IRequestPayloadStep
{
    public string Name => "strip-stream-options";

    public bool Applies(RequestPayloadContext context)
    {
        bool copilotCompatible = context.Mapping?.EnableCopilotCompatibility ?? true;
        (bool hadStreamOptions, bool includeUsage) = RequestPayloadReads.ReadStreamOptions(context.Payload);
        bool strip = copilotCompatible && hadStreamOptions;

        context.StreamOptions = new StreamOptionsInfo(hadStreamOptions, includeUsage, strip);
        return strip;
    }

    public void Apply(RequestPayloadContext context)
    {
        string? key = context.Payload
            .Select(pair => pair.Key)
            .FirstOrDefault(k => k.Equals("stream_options", StringComparison.OrdinalIgnoreCase));

        if (key is not null)
            context.Payload.Remove(key);

        context.Rewritten = true;

        Log.Debug(
            "Stripping stream_options from the upstream request for {Model} (include_usage={IncludeUsage}); the proxy will synthesize the terminal usage chunk",
            context.EffectiveModel, context.StreamOptions.IncludeUsage);
    }
}

/// <summary>
/// Scans the message array for the two conversation shapes that force a rewrite: consecutive leading
/// system messages (strict chat templates reject a second system message) and a trailing assistant
/// prefill (several upstreams reject it when thinking mode is on). Records the findings for the
/// steps that act on them, rather than only reporting its own applicability.
/// </summary>
internal sealed class ConversationShapeStep : IRequestPayloadStep
{
    public string Name => "conversation-shape";

    public bool Applies(RequestPayloadContext context) => true;

    public void Apply(RequestPayloadContext context)
    {
        context.ShouldInjectInstructions = !string.IsNullOrWhiteSpace(context.InjectedInstructions);

        if (context.Payload["messages"] is not JsonArray messages)
            return;

        int leadingSystem = RequestPayloadReads.LeadingSystemCount(messages);
        context.HasConsecutiveSystemMessages = leadingSystem > 1;
        context.HasTrailingAssistantPrefill =
            context.ApplyThinkingCompatibility
            && messages.Count > 0
            && RequestPayloadReads.IsAssistantResponsePrefill(messages[^1]);
    }
}

/// <summary>
/// Folds the instruction text and every leading system message into exactly one system message, and
/// removes a trailing assistant prefill. Delegates the composition rule to
/// <see cref="SystemPromptComposer"/> so all three request paths keep sharing one definition.
/// </summary>
internal sealed class ComposeMessagesStep : IRequestPayloadStep
{
    public string Name => "compose-messages";

    public bool Applies(RequestPayloadContext context)
        => context.HasConsecutiveSystemMessages
            || context.HasTrailingAssistantPrefill
            || context.ShouldInjectInstructions;

    public void Apply(RequestPayloadContext context)
    {
        if (context.Payload["messages"] is not JsonArray messages)
            return;

        int leadingSystemCount = RequestPayloadReads.LeadingSystemCount(messages);
        bool recomposeSystem = SystemPromptComposer.ShouldRecompose(context.InjectedInstructions, leadingSystemCount);

        JsonArray rebuilt = [];

        if (recomposeSystem)
        {
            rebuilt.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = SystemPromptComposer.Merge(
                    context.InjectedInstructions,
                    LeadingSystemContents(messages, leadingSystemCount)),
            });
        }

        // When nothing is being recomposed the client's lone system message is forwarded verbatim so
        // any extra properties it carries survive.
        for (int i = recomposeSystem ? leadingSystemCount : 0; i < messages.Count; i++)
        {
            JsonNode? message = messages[i];

            if (context.HasTrailingAssistantPrefill
                && i == messages.Count - 1
                && RequestPayloadReads.IsAssistantResponsePrefill(message))
            {
                continue;
            }

            rebuilt.Add(message?.DeepClone());
        }

        context.Payload["messages"] = rebuilt;
        context.Rewritten = true;

        if (context.HasTrailingAssistantPrefill)
            Note(context, "messages: removed trailing assistant prefill (thinking compatibility)");

        if (recomposeSystem)
        {
            if (context.Mapping?.InstructionSetName is not null)
                Note(context, DebugNotes.InstructionInjection(context.Mapping.InstructionSetName));

            if (leadingSystemCount > 1)
                Note(context, "messages: merged consecutive leading system messages into a single system message");
        }
    }

    private static List<string> LeadingSystemContents(JsonArray messages, int count)
    {
        List<string> contents = [];
        for (int i = 0; i < count && i < messages.Count; i++)
        {
            if (messages[i] is JsonObject message
                && message["content"] is JsonValue content
                && content.TryGetValue(out string? text)
                && text is not null)
            {
                contents.Add(text);
            }
        }

        return contents;
    }

    private static void Note(RequestPayloadContext context, string note)
        => context.PendingNotes.Add(note);
}