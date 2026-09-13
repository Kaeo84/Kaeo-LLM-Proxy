using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Phase-B golden parity: the IR-routed request translation (OllamaRequestTranslation,
/// behind AppSettings.UseIrTranslation) must produce structurally identical upstream
/// messages to the legacy mapper (MapMessagesWithToolCorrelation) - id preservation,
/// 8-hex id generation, and FIFO correlation of unlabelled tool replies. Generated ids
/// are normalized to GEN so runs are comparable; the pairing itself is asserted per path.
/// </summary>
public class OllamaIrParityTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Canonical(List<LlamaCppMessage> messages)
        => System.Text.RegularExpressions.Regex.Replace(
            JsonSerializer.Serialize(messages, Options),
            @"""[0-9a-f]{8}""",
            "GEN");

    private static void AssertParity(List<OllamaMessage> source)
        => Assert.Equal(
            Canonical(OllamaProxyHandler.MapMessagesWithToolCorrelation(source)),
            Canonical(OllamaRequestTranslation.ToLlamaCppMessages(source)));

    private static OllamaToolCall Call(string? id, string name, object arguments)
        => new()
        {
            Id = id,
            Function = new OllamaToolCallFunction { Name = name, Arguments = arguments },
        };

    [Fact]
    public void PlainConversationMatchesLegacy()
    {
        AssertParity(
        [
            new OllamaMessage("system", "You are helpful."),
            new OllamaMessage("user", "hi"),
            new OllamaMessage("assistant", "hello"),
            new OllamaMessage("user", "again"),
        ]);
    }

    [Fact]
    public void ExplicitIdsAndCorrelatedRepliesMatchLegacy()
    {
        AssertParity(
        [
            new OllamaMessage("user", "check config"),
            new OllamaMessage("assistant", null)
            {
                ToolCalls =
                [
                    Call("call-a", "view", JsonSerializer.Deserialize<JsonElement>("""{"path":"app.config"}""")),
                    Call("call-b", "edit", JsonSerializer.Deserialize<JsonElement>("""{"n":3,"ok":true}""")),
                ],
            },
            new OllamaMessage("tool", "config: port 11434") { ToolCallId = "call-a" },
            new OllamaMessage("tool", "edited"), // unlabelled -> correlates FIFO to the remaining call
        ]);
    }

    [Fact]
    public void GeneratedIdsStillCorrelateFifoIdentically()
    {
        var source = new List<OllamaMessage>
        {
            new("user", "go"),
            new OllamaMessage("assistant", "working")
            {
                ToolCalls =
                [
                    Call(null, "view", JsonSerializer.Deserialize<JsonElement>("""{"path":"a.txt"}""")),
                    Call(null, "run", JsonSerializer.Deserialize<JsonElement>("{}")),
                ],
            },
            new OllamaMessage("tool", "r1"),
            new OllamaMessage("tool", "r2"),
        };

        List<LlamaCppMessage> legacy = OllamaProxyHandler.MapMessagesWithToolCorrelation(source);
        List<LlamaCppMessage> ir = OllamaRequestTranslation.ToLlamaCppMessages(source);

        foreach (List<LlamaCppMessage> path in new[] { legacy, ir })
        {
            string firstId = path[1].ToolCalls![0].Id;
            string secondId = path[1].ToolCalls![1].Id;
            Assert.Matches("^[0-9a-f]{8}$", firstId);
            Assert.Matches("^[0-9a-f]{8}$", secondId);
            Assert.NotEqual(firstId, secondId);
            Assert.Equal(firstId, path[2].ToolCallId);
            Assert.Equal(secondId, path[3].ToolCallId);
        }

        Assert.Equal(Canonical(legacy), Canonical(ir));
    }

    [Fact]
    public void StringEncodedArgumentsMatchLegacy()
    {
        AssertParity(
        [
            new OllamaMessage("user", "find"),
            new OllamaMessage("assistant", "calling")
            {
                ToolCalls = [Call("call-s", "search", """{"q":"net"}""")],
            },
        ]);
    }

    [Fact]
    public void NullAndEmptyContentFidelityMatchesLegacy()
    {
        AssertParity(
        [
            new OllamaMessage("user", ""),
            new OllamaMessage("assistant", null),
            new OllamaMessage("user", "text"),
        ]);
    }

    [Fact]
    public void RequestSideThinkingIsDroppedLikeLegacy()
    {
        AssertParity(
        [
            new OllamaMessage("assistant", "the answer") { Thinking = "secret trace" },
            new OllamaMessage("user", "next"),
        ]);
    }

    [Fact]
    public void InboundIrCarriesTypedContents()
    {
        var assistant = new OllamaMessage("assistant", "calling")
        {
            Thinking = "plan",
            ToolCalls = [Call("c1", "view", JsonSerializer.Deserialize<JsonElement>("""{"p":"x"}"""))],
        };

        List<ChatMessage> ir = OllamaRequestTranslation.ToChatMessages(
        [
            new OllamaMessage("user", "hello"),
            assistant,
            new OllamaMessage("tool", "result text") { ToolCallId = "c1" },
        ]);

        Assert.Equal("hello", Assert.Single(ir[0].Contents.OfType<TextContent>()).Text);
        Assert.Equal("plan", Assert.Single(ir[1].Contents.OfType<TextReasoningContent>()).Text);
        FunctionCallContent call = Assert.Single(ir[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("c1", call.CallId);
        Assert.Equal("view", call.Name);
        Assert.Equal("x", call.Arguments!["p"]);
        FunctionResultContent result = Assert.Single(ir[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("c1", result.CallId);
        Assert.Equal("result text", result.Result as string);
    }
}
