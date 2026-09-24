using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// The single consistent transformer for an OpenAI-compatible request payload: an ordered list of
/// <see cref="IRequestPayloadStep"/> instances applied to one parsed body.
/// </summary>
/// <remarks>
/// Phase B4 of Plans/20260914-proxy-meai-phase-b-design.md. The legacy
/// <c>OllamaProxyHandler.NormalizeRequestBody</c> performed every payload mutation inline in one
/// hand-ordered writer pass, which made each new provider quirk another branch in a monolith. Here
/// each concern is a named step and the order lives in one place, so adding a provider rule means
/// adding a step rather than threading a condition through existing code.
///
/// Byte fidelity is a hard constraint: when no step reports that it applies, the pipeline returns
/// the client's original text verbatim rather than a re-serialization of it, because re-emitting
/// JSON changes key order, number formatting, and whitespace.
/// </remarks>
internal static class RequestPayloadPipeline
{
    /// <summary>The outcome of a pipeline run.</summary>
    /// <param name="Body">The upstream-bound body: the rewritten JSON, or the original text.</param>
    /// <param name="StreamOptions">What the client asked for in <c>stream_options</c>, for the response path.</param>
    internal readonly record struct Result(string Body, StreamOptionsInfo StreamOptions);

    /// <summary>
    /// Runs <paramref name="steps"/> over the client's body. Returns the rewritten JSON, or the
    /// original text when nothing applied or the body could not be parsed.
    /// </summary>
    public static Result Run(string json, AppSettings settings, RequestLog log, IReadOnlyList<IRequestPayloadStep> steps)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // Non-JSON or malformed body: forward untouched, matching the legacy behaviour.
            return new Result(json, StreamOptionsInfo.None);
        }

        if (payload is null)
            return new Result(json, StreamOptionsInfo.None);

        RequestPayloadContext context = new()
        {
            OriginalJson = json,
            Payload = payload,
            Settings = settings,
            Log = log,
        };

        foreach (IRequestPayloadStep step in steps)
        {
            if (!step.Applies(context))
                continue;

            try
            {
                step.Apply(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing step must not take the request down: forward with the remaining steps
                // applied, having recorded which step did not complete.
                Log.Warning(ex, "Request payload step {Step} failed; continuing with the payload as it stands", step.Name);
                context.Log.DebugSummary = AppendNote(context.Log.DebugSummary, $"payload step '{step.Name}' failed: {ex.Message}");
            }
        }

        if (!context.Rewritten)
            return new Result(json, context.StreamOptions);

        // Debug mode: record every settings-driven override the steps applied, plus any notes the
        // steps collected. The order matches the legacy summary so existing logs stay comparable.
        if (settings.DebugMode)
            log.DebugSummary = BuildDebugSummary(context);

        return new Result(payload.ToJsonString(PayloadWriteOptions), context.StreamOptions);
    }

    /// <summary>
    /// Composes the audit trail for a rewritten passthrough body. Only reached when a rewrite
    /// happened, so a summary is always produced.
    /// </summary>
    private static string BuildDebugSummary(RequestPayloadContext context)
    {
        System.Text.StringBuilder sb = new();

        if (context.CompactRedirected)
            sb.AppendLine(DebugNotes.ContextSummarizeRedirectPassthrough(context.OriginalModel, context.EffectiveModel));

        sb.AppendLine(DebugNotes.ModelResolution(
            context.EffectiveModel,
            context.ResolvedModel,
            !string.Equals(context.EffectiveModel, context.ResolvedModel, StringComparison.OrdinalIgnoreCase)));

        sb.AppendLine(DebugNotes.SamplingDecision(
            "temperature", context.TemperaturePriority, context.ClientTemperature, (float)context.ProxyTemperature));
        sb.AppendLine(DebugNotes.SamplingDecision(
            "repeat_penalty", context.RepeatPenaltyPriority, context.ClientRepeatPenalty, (float)context.ProxyRepeatPenalty));
        sb.AppendLine(DebugNotes.ReasoningEffortDecision(
            context.ReasoningEffortPriority,
            context.ClientReasoningEffort,
            context.ProxyReasoningEffort,
            context.ReasoningEffortFormat));

        if (!string.IsNullOrWhiteSpace(context.InjectedInstructions))
            sb.AppendLine(DebugNotes.InstructionInjection(context.Mapping?.InstructionSetName ?? string.Empty));

        // Ordering note: the context flags are reported here even though the legacy summary derived
        // them separately, so the audit trail names the same events in the same order.
        if (context.HasConsecutiveSystemMessages)
            sb.AppendLine("messages: merged consecutive leading system messages into a single system message");
        if (context.HasTrailingAssistantPrefill)
            sb.AppendLine("messages: removed trailing assistant prefill (thinking compatibility)");

        foreach (string note in context.PendingNotes)
        {
            // Skip notes already emitted above so a merged system message is not reported twice.
            if (note.Contains("merged consecutive leading system messages", StringComparison.Ordinal)
                || note.Contains("removed trailing assistant prefill", StringComparison.Ordinal)
                || note.Contains("injected instruction set", StringComparison.Ordinal))
            {
                continue;
            }

            sb.AppendLine(note);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Serialization options for the rewritten body. Nulls are omitted to match the legacy writer,
    /// which never emitted a null it was not handed.
    /// </summary>
    public static readonly JsonSerializerOptions PayloadWriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string AppendNote(string? existing, string note)
        => string.IsNullOrEmpty(existing) ? note : existing + "\n" + note;

    /// <summary>
    /// The steps in the order they must run. Ordering is load-bearing: the model resolution decides
    /// which mapping governs, so the sampling and reasoning steps read that mapping's settings; the
    /// conversation-shape scan must follow the model resolution because the prefill rule depends on
    /// the effective model's thinking-compatibility setting.
    /// </summary>
    public static IReadOnlyList<IRequestPayloadStep> DefaultSteps { get; } =
    [
        new ResolveModelStep(),
        new ConversationShapeStep(),
        new StreamOptionsStep(),
        new SamplingPriorityStep(),
        new ReasoningEffortStep(),
        new RewriteModelNameStep(),
        new ComposeMessagesStep(),
    ];
}