using System.Text;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Guarantees that an OpenAI-compatible SSE chat-completion stream ends with the terminal events a
/// conformant client awaits. Observes every forwarded line and, when the upstream closed without
/// sending them, supplies the missing frames:
/// <list type="bullet">
///   <item><description>
///     a <c>usage</c> chunk when the client requested one via
///     <c>stream_options.include_usage</c> and the upstream never reported usage, then
///   </description></item>
///   <item><description>the <c>data: [DONE]</c> terminator.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Clients built on Microsoft.Extensions.AI (e.g. Visual Studio Copilot) await the terminal stream
/// event and block indefinitely when it never arrives. Upstream <c>/v1</c> servers do not reliably
/// emit <c>[DONE]</c>, and several local servers reject or ignore <c>stream_options</c> outright — so
/// when the proxy strips that parameter it also becomes responsible for producing the usage chunk the
/// client asked for. This type is pure and side-effect free: callers own the writing, which keeps it
/// unit-testable without an <see cref="System.Net.HttpListener"/>.
/// <para>
/// Not thread-safe. One instance per stream, used from the single copy loop that owns it.
/// </para>
/// </remarks>
internal sealed class OpenAiStreamTerminator
{
    /// <summary>
    /// The SSE terminator payload. Compared after stripping the <c>data:</c> prefix so both
    /// <c>data: [DONE]</c> and <c>data:[DONE]</c> are recognised.
    /// </summary>
    private const string DonePayload = "[DONE]";

    private const string DataPrefix = "data:";

    private readonly bool _includeUsage;
    private readonly string _model;

    // Partial line carried across Feed calls on the byte-oriented path, where chunk boundaries
    // are unrelated to SSE framing.
    private readonly StringBuilder _pending = new();

    // Identity of the synthesized usage chunk. Taken from the first observed upstream chunk so the
    // frame the proxy invents is indistinguishable from the ones the client already received;
    // falls back to generated values when the upstream sent no parseable chunk at all.
    private string? _chunkId;
    private long? _created;
    private bool _identityCaptured;

    /// <summary>True once a <c>data: [DONE]</c> frame has been observed on the stream.</summary>
    public bool DoneSeen { get; private set; }

    /// <summary>
    /// True once a chunk carrying a non-null <c>usage</c> object has been observed. Under
    /// <c>include_usage</c> the upstream emits <c>"usage": null</c> on every token delta and a real
    /// object only on the final chunk; System.Text.Json represents a JSON null as an absent
    /// <see cref="JsonNode"/>, so a non-null lookup means usage was genuinely reported.
    /// </summary>
    public bool UsageSeen { get; private set; }

    /// <summary>
    /// Creates a terminator for one stream.
    /// </summary>
    /// <param name="model">
    /// Model name reported on the synthesized usage chunk. Should be the name the client asked for,
    /// so the frame matches the ones already streamed.
    /// </param>
    /// <param name="includeUsage">
    /// Whether the client requested <c>stream_options.include_usage</c>. When false no usage chunk is
    /// synthesized — only the <c>[DONE]</c> terminator is guaranteed.
    /// </param>
    public OpenAiStreamTerminator(string model, bool includeUsage)
    {
        _model = model ?? string.Empty;
        _includeUsage = includeUsage;
    }

    /// <summary>
    /// Observes one complete SSE line as it is forwarded. Lines that are not <c>data:</c> payloads
    /// (comments, keep-alives, blank separators) are ignored.
    /// </summary>
    public void ObserveLine(string? line)
    {
        if (string.IsNullOrEmpty(line) || DoneSeen)
            return;

        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith(DataPrefix, StringComparison.Ordinal))
            return;

        string data = trimmed[DataPrefix.Length..].Trim();
        if (data.Length == 0)
            return;

        if (string.Equals(data, DonePayload, StringComparison.Ordinal))
        {
            DoneSeen = true;
            return;
        }

        // Ordinary token deltas are skipped so the hot path does not pay for a JSON parse per
        // token. A frame is parsed when it could carry usage, or while the chunk identity is still
        // unknown (the first parseable chunk supplies it). Mirrors SseUsageSniffer's guard.
        if (_identityCaptured && !data.Contains("\"usage\"", StringComparison.Ordinal))
            return;

        try
        {
            if (JsonNode.Parse(data) is not JsonObject root)
                return;

            if (!_identityCaptured)
            {
                if (root["id"] is JsonValue idValue && idValue.TryGetValue<string>(out string? id))
                    _chunkId = id;
                if (root["created"] is JsonValue createdValue && createdValue.TryGetValue<long>(out long created))
                    _created = created;

                _identityCaptured = true;
            }

            if (root["usage"] is not null)
                UsageSeen = true;
        }
        catch (System.Text.Json.JsonException)
        {
            // An unparseable frame cannot be interpreted; leave the flags untouched so the
            // terminator still runs at end of stream.
        }
    }

    /// <summary>
    /// Observes a decoded slice of the forwarded stream on the byte-oriented path, buffering any
    /// trailing partial line until the next call. Call <see cref="Flush"/> once the stream ends.
    /// </summary>
    public void Feed(string text)
    {
        if (text.Length == 0)
            return;

        _pending.Append(text);

        while (true)
        {
            string buffered = _pending.ToString();
            int newline = buffered.IndexOf('\n');
            if (newline < 0)
                break;

            _pending.Remove(0, newline + 1);
            ObserveLine(buffered[..newline]);
        }
    }

    /// <summary>Processes any trailing partial line buffered by <see cref="Feed"/>.</summary>
    public void Flush()
    {
        if (_pending.Length > 0)
        {
            ObserveLine(_pending.ToString());
            _pending.Clear();
        }
    }

    /// <summary>
    /// Returns the SSE frames that must be appended to close the stream correctly, each already
    /// terminated with a blank line so callers can write them verbatim. Empty when the upstream
    /// already delivered everything the client is waiting for.
    /// </summary>
    /// <param name="promptTokens">Prompt tokens for the synthesized usage chunk. Ignored when usage was not requested or already reported.</param>
    /// <param name="completionTokens">Completion tokens for the synthesized usage chunk. Ignored when usage was not requested or already reported.</param>
    public IReadOnlyList<string> BuildTerminalFrames(int promptTokens, int completionTokens)
    {
        List<string> frames = [];

        // Once [DONE] has been forwarded the stream is over as far as the client is concerned, so
        // appending a usage chunk after it would be protocol-invalid (and discarded). The usage
        // chunk is only synthesizable while the terminator is still pending.
        if (!DoneSeen)
        {
            if (_includeUsage && !UsageSeen)
                frames.Add($"data: {BuildUsageChunk(promptTokens, completionTokens)}\n\n");

            frames.Add($"data: {DonePayload}\n\n");
        }

        return frames;
    }

    /// <summary>
    /// Builds the terminal usage-only chunk OpenAI specifies for <c>include_usage</c>: an empty
    /// <c>choices</c> array plus the <c>usage</c> totals. Token counts already captured from the
    /// upstream are reported so the client's own accounting stays accurate even though the upstream
    /// dropped the frame.
    /// </summary>
    private string BuildUsageChunk(int promptTokens, int completionTokens)
    {
        int prompt = Math.Max(0, promptTokens);
        int completion = Math.Max(0, completionTokens);

        JsonObject chunk = new()
        {
            ["id"] = _chunkId ?? $"chatcmpl-kaeo-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = _created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = _model,
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completion,
                ["total_tokens"] = prompt + completion,
            },
        };

        return chunk.ToJsonString();
    }
}
