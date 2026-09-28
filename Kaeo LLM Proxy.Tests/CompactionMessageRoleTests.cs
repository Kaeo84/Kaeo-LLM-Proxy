using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins that a compacted conversation never carries more than one system message, at any stage.
/// </summary>
/// <remarks>
/// The failure this exists because of: a Copilot conversation contains several system messages, and
/// every chunk is a slice of that conversation. Our summarization request already carries a system
/// message of its own (the summarizer prompt), so a chunk system message anywhere in the request is a
/// second one. llama.cpp's chat template rejected it outright —
/// <c>"Jinja Exception: System message must be at the beginning."</c> — returning HTTP 500 and losing
/// that chunk. With enough unlucky slicing every chunk fails, compaction returns null, and the
/// oversized body is forwarded un-compacted, which is the exact failure compaction exists to prevent.
/// </remarks>
public class CompactionMessageRoleTests
{
    private const string SummarizerMarker = "## Tool activity";

    /// <summary>Builds an OpenAI-style request body from (role, content) pairs.</summary>
    private static string BuildBody(params (string Role, string Content)[] messages)
    {
        var list = messages.Select(m => new { role = m.Role, content = m.Content }).ToList();
        return JsonSerializer.Serialize(new { model = "main", messages = list });
    }

    /// <summary>Counts system-role messages in a serialized request body's messages array.</summary>
    private static int CountSystemMessages(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Count(m => m.TryGetProperty("role", out JsonElement r)
                && r.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── The summarization request carries exactly one system message ──────

    [Fact]
    public async Task SummarizationRequest_HasExactlyOneSystemMessage_WhenAChunkStartsOnAClientSystemMessage()
    {
        // The reported case: chunk 1 was the slice that began on Copilot's system prompt.
        string body = BuildBody(
            ("system", "Copilot session instructions"),
            ("user", "first ask"),
            ("assistant", "first answer"),
            ("user", "second ask"));

        string? captured = null;
        using HttpClient http = new(new StubChatHandler(req => captured = req));

        AutoCompactionService service = new(http);
        await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 200, "compact-model", 100000, 100000, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(1, CountSystemMessages(captured!));
    }

    [Fact]
    public async Task SummarizationRequest_HasExactlyOneSystemMessage_WhenAChunkContainsOneMidWay()
    {
        // A slice can also contain a later system message without starting on one.
        string body = BuildBody(
            ("user", "first ask"),
            ("assistant", "first answer"),
            ("system", "injected context"),
            ("user", "second ask"),
            ("assistant", "second answer"));

        string? captured = null;
        using HttpClient http = new(new StubChatHandler(req => captured = req));

        AutoCompactionService service = new(http);
        await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 200, "compact-model", 100000, 100000, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(1, CountSystemMessages(captured!));
    }

    [Fact]
    public async Task SummarizationRequest_KeepsTheClientSystemMessageText()
    {
        // Demotion preserves the text: to a summarizer that content is conversation, not instruction,
        // so dropping it would lose decisions recorded in the client's own system prompt.
        string body = BuildBody(
            ("system", "PROJECT CONVENTION: tabs not spaces"),
            ("user", "hi"));

        string? captured = null;
        using HttpClient http = new(new StubChatHandler(req => captured = req));

        AutoCompactionService service = new(http);
        await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 200, "compact-model", 100000, 100000, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("PROJECT CONVENTION", captured!);
    }

    [Fact]
    public async Task SummarizationRequest_StillCarriesTheSummarizerInstructions()
    {
        // The demotion must not displace the summarizer prompt, which is the one legitimate system
        // message in the request.
        string body = BuildBody(("system", "client prompt"), ("user", "hi"));

        string? captured = null;
        using HttpClient http = new(new StubChatHandler(req => captured = req));

        AutoCompactionService service = new(http);
        await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 200, "compact-model", 100000, 100000, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains(SummarizerMarker, captured!);
    }

    [Fact]
    public async Task SummarizationRequest_HonorsAConfiguredInstructionSet()
    {
        // The Settings-tab instruction set replaces the summarizer prompt, and must survive the
        // demotion path unchanged.
        string body = BuildBody(("system", "client prompt"), ("user", "hi"));

        string? captured = null;
        using HttpClient http = new(new StubChatHandler(req => captured = req));

        AutoCompactionService service = new(http);
        service.Configure("CUSTOM SUMMARIZER INSTRUCTION", 0);
        await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 200, "compact-model", 100000, 100000, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("CUSTOM SUMMARIZER INSTRUCTION", captured!);
        Assert.DoesNotContain(SummarizerMarker, captured!);
    }

    // ── The compacted body Copilot receives also carries one ─────────────

    [Fact]
    public async Task CompactedBody_HasExactlyOneSystemMessage()
    {
        // The pinned prompt plus the summary is message[0]; a later system message in the trailing
        // turn would be a second one and would be rejected on the next turn.
        //
        // The conversation is padded so compaction genuinely reduces it: CompactAsync discards a
        // result that is not smaller than the original, so a tiny body would never be observable.
        string padding = new string('x', 40_000);
        string body = BuildBody(
            ("system", "pinned instructions"),
            ("user", "old ask " + padding),
            ("system", "later system message"),
            ("user", "latest ask"));

        using HttpClient http = new(new StubChatHandler(_ => { }, summarize: true));

        AutoCompactionService service = new(http);
        string? compacted = await service.CompactAsync(
            Mapping(), body, "session", "http://localhost:8080", null, 30,
            maxTokensPerChunk: 2000, "compact-model", 100_000, 100_000, CancellationToken.None);

        Assert.NotNull(compacted);
        Assert.Equal(1, CountSystemMessages(compacted!));

        // The pinned prompt must still be the leading system message.
        using JsonDocument doc = JsonDocument.Parse(compacted!);
        JsonElement first = doc.RootElement.GetProperty("messages").EnumerateArray().First();
        Assert.Equal("system", first.GetProperty("role").GetString());
        Assert.Contains("pinned instructions", first.GetProperty("content").GetString()!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ModelMapping Mapping()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
        };
        mapping.EnsureId();
        return mapping;
    }

    /// <summary>
    /// Captures the summarization request body and replies with a minimal completion, so the service
    /// exercises its real request-construction path.
    /// </summary>
    private sealed class StubChatHandler : HttpMessageHandler
    {
        private readonly Action<string> _capture;
        private readonly bool _summarize;

        public StubChatHandler(Action<string> capture, bool summarize = false)
        {
            _capture = capture;
            _summarize = summarize;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _capture(body);

            // "summarize" mode answers the combine step too, so a multi-chunk run reaches body
            // building rather than stopping at the first summary.
            string content = _summarize
                ? "Combined checkpoint summary.\n\n## Tool activity\n- none"
                : "Chunk checkpoint summary.\n\n## Tool activity\n- none";

            string json = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content } } },
            });

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}