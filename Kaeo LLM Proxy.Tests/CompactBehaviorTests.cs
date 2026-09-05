using System.Net;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Behavior tests for the AutoCompactionService chunked map-reduce pipeline, driven
/// end-to-end against a stubbed upstream chat-completions server.
/// </summary>
public class CompactBehaviorTests
{
    /// <summary>Upstream stub that answers every POST with a canned chat completion.</summary>
    private sealed class StubChatHandler : HttpMessageHandler
    {
        private readonly string _reply;

        public List<string> RequestBodies { get; } = [];

        public StubChatHandler(string reply = """{"choices":[{"message":{"content":"stub summary"}}]}""")
            => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Upstream stub that always fails, to exercise circuit-breaker behavior.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"message":"boom"}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ModelMapping NewMapping() => new()
    {
        ProxyName = "big-model",
        ModelName = "upstream.gguf",
        UpstreamUrl = "http://localhost:8080",
        ContextWindowTokens = 65536,
        ProactiveOverflowTokens = 40000,
        AutoCompactPaths = AutoCompactPaths.Both,
    };

    private static string BuildBody(params (string Role, string Content)[] messages)
    {
        var list = messages.Select(m => new { role = m.Role, content = m.Content }).ToList();
        return JsonSerializer.Serialize(new { model = "upstream.gguf", messages = list });
    }

    private static async Task<string?> CompactAsync(
        AutoCompactionService service, ModelMapping mapping, string body, CompactionFormat format)
    {
        return await service.CompactAsync(
            mapping,
            body,
            $"test:{Guid.NewGuid()}",
            "http://localhost:8080",
            apiKey: null,
            timeoutSeconds: 30,
            maxTokensPerChunk: 1000,
            compactModelName: "upstream.gguf",
            targetModelContextWindow: 65536,
            compactModelContextWindow: 65536,
            CancellationToken.None,
            format);
    }

    [Fact]
    public async Task CompactAsync_ProxyFormat_PreservesSystemInstructionsAndLastUserMessage()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string body = BuildBody(
            ("system", "pinned instructions"),
            ("user", "old question " + new string('a', 120_000)),
            ("assistant", "old answer"),
            ("user", "What is the capital of France?"));

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.NotNull(compacted);
        using JsonDocument doc = JsonDocument.Parse(compacted);

        Assert.Equal("upstream.gguf", doc.RootElement.GetProperty("model").GetString());
        List<JsonElement> msgs = [.. doc.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.True(msgs.Count >= 2);

        string firstContent = msgs[0].GetProperty("content").GetString()!;
        Assert.Contains("pinned instructions", firstContent);
        Assert.Contains("Previous conversation summary", firstContent);
        Assert.Contains("What is the capital of France?", msgs[^1].GetProperty("content").GetString());
        Assert.NotEmpty(stub.RequestBodies);
    }

    [Fact]
    public async Task CompactAsync_OllamaFormat_EmitsToolCallSummaryPairAndKeepsLastQuestion()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string body = BuildBody(
            ("system", "pinned instructions"),
            ("user", "old question " + new string('b', 120_000)),
            ("assistant", "old answer"),
            ("user", "Explain recursion briefly."));

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Ollama);

        Assert.NotNull(compacted);
        using JsonDocument doc = JsonDocument.Parse(compacted);
        List<JsonElement> msgs = [.. doc.RootElement.GetProperty("messages").EnumerateArray()];

        Assert.Contains(msgs, m => m.GetProperty("role").GetString() == "system"
            && m.GetProperty("content").GetString() == "pinned instructions");
        Assert.Contains(msgs, m => m.GetProperty("role").GetString() == "assistant"
            && m.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString() == "session_summary");
        Assert.Contains(msgs, m => m.GetProperty("role").GetString() == "tool"
            && m.GetProperty("tool_name").GetString() == "session_summary"
            && m.GetProperty("content").GetString()!.StartsWith("<conversation_summary>"));
        Assert.Contains(msgs, m => m.GetProperty("role").GetString() == "user"
            && m.GetProperty("content").GetString() == "Explain recursion briefly.");
    }

    [Fact]
    public async Task CompactAsync_LargeConversation_SplitsIntoMultipleChunkRequests()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string chunkFiller = new string('c', 20_000);
        string body = BuildBody(
            ("user", $"first {chunkFiller}"),
            ("assistant", $"answer {chunkFiller}"),
            ("user", $"second {chunkFiller}"),
            ("assistant", $"answer2 {chunkFiller}"),
            ("user", "final question?"));

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.NotNull(compacted);
        // maxTokensPerChunk is 1000; ~5 messages of ~6500 estimated tokens each force
        // several chunk summarization requests plus combine passes.
        Assert.True(stub.RequestBodies.Count >= 2,
            $"expected multiple chunk summarization requests, got {stub.RequestBodies.Count}");
    }

    [Fact]
    public async Task CompactAsync_BodyWithoutMessages_ReturnsNullWithoutCallingUpstream()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string body = """{"model":"upstream.gguf","prompt":"no messages array here"}""";

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.Null(compacted);
        Assert.Empty(stub.RequestBodies);
    }

    [Fact]
    public async Task CompactAsync_SummaryNotSmallerThanOriginal_ReturnsNull()
    {
        StubChatHandler stub = new("""{"choices":[{"message":{"content":"xx"}}]}""".Replace("xx", new string('z', 300_000)));
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string body = BuildBody(("user", "tiny question"));

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.Null(compacted);
    }

    [Fact]
    public async Task CompactAsync_OpensCircuitAfterThreeFailedAttempts_AndSkipsFourthCall()
    {
        FailingHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);
        string sessionKey = "breaker-test";

        string body = BuildBody(
            ("user", "question " + new string('d', 20_000)),
            ("user", "still here?"));

        for (int i = 0; i < 3; i++)
        {
            string? attempt = await service.CompactAsync(
                NewMapping(), body, sessionKey, "http://localhost:8080", null, 30,
                1000, "upstream.gguf", 65536, 65536, CancellationToken.None);
            Assert.Null(attempt);
        }

        int callsBeforeFourth = stub.CallCount;
        string? fourth = await service.CompactAsync(
            NewMapping(), body, sessionKey, "http://localhost:8080", null, 30,
            1000, "upstream.gguf", 65536, 65536, CancellationToken.None);

        Assert.Null(fourth);
        Assert.Equal(callsBeforeFourth, stub.CallCount);
    }

    [Fact]
    public async Task CompactAsync_SendsUpstreamModelNameInSummarizationRequests()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        string body = BuildBody(("user", "question " + new string('e', 20_000)), ("user", "follow up?"));

        await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.NotEmpty(stub.RequestBodies);
        foreach (string request in stub.RequestBodies)
        {
            using JsonDocument doc = JsonDocument.Parse(request);
            Assert.Equal("upstream.gguf", doc.RootElement.GetProperty("model").GetString());
        }
    }

    [Fact]
    public async Task CompactAsync_FlattensToolCallsIntoToolcallsBlock()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        var body = JsonSerializer.Serialize(new
        {
            model = "upstream.gguf",
            messages = new object[]
            {
                new { role = "user", content = "old ask " + new string('f', 60_000) },
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = "call_1",
                            type = "function",
                            function = new { name = "view", arguments = """{"path":"app.config"}""" }
                        }
                    }
                },
                new { role = "tool", tool_call_id = "call_1", content = "config contents: port 11434" }
            }
        });

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.NotNull(compacted);
        Assert.NotEmpty(stub.RequestBodies);
        string summaryRequest = stub.RequestBodies[0];
        Assert.Contains("<toolcalls>", summaryRequest);
        Assert.Contains("view", summaryRequest);
        Assert.DoesNotContain("tool_call_id", summaryRequest);
    }

    [Fact]
    public async Task CompactAsync_ProxyFormat_KeepsTrailingToolPairAfterSummary()
    {
        StubChatHandler stub = new();
        using HttpClient http = new(stub);
        AutoCompactionService service = new(http);

        var body = JsonSerializer.Serialize(new
        {
            model = "upstream.gguf",
            messages = new object[]
            {
                new { role = "user", content = "old ask " + new string('g', 60_000) },
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = "call_2",
                            type = "function",
                            function = new { name = "edit", arguments = """{"path":"config.json"}""" }
                        }
                    }
                },
                new { role = "tool", tool_call_id = "call_2", content = "edit successful" }
            }
        });

        string? compacted = await CompactAsync(service, NewMapping(), body, CompactionFormat.Proxy);

        Assert.NotNull(compacted);
        using JsonDocument doc = JsonDocument.Parse(compacted);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();

        bool hasAssistantWithToolCalls = msgs.Any(m =>
            m.GetProperty("role").GetString() == "assistant" &&
            m.TryGetProperty("tool_calls", out _));
        bool hasToolResult = msgs.Any(m =>
            m.GetProperty("role").GetString() == "tool" &&
            m.GetProperty("tool_call_id").GetString() == "call_2");

        Assert.True(hasAssistantWithToolCalls, "Compacted body should preserve assistant message with tool_calls");
        Assert.True(hasToolResult, "Compacted body should preserve tool result with matching tool_call_id");
    }
}
