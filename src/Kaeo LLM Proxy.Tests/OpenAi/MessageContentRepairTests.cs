using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// A missing or null <c>content</c> on an assistant tool-call turn is legal for OpenAI and llama.cpp
/// but a hard 400 on strict providers (Alibaba DashScope / Qwen Cloud answer
/// <c>invalid_parameter_error</c> "The content field is a required field"). These tests pin the
/// repair that keeps such a request forwardable, and pin that a body needing no repair is still
/// forwarded byte-for-byte.
/// </summary>
public class MessageContentRepairTests
{
    private static AppSettings SettingsWith(Action<AppSettings>? configure = null)
    {
        AppSettings settings = new();
        configure?.Invoke(settings);
        return settings;
    }

    private static ModelMapping AddMapping(AppSettings settings, string proxyName, Action<ModelMapping>? configure = null)
    {
        ModelMapping mapping = new()
        {
            ProxyName = proxyName,
            ModelName = proxyName + "-upstream",
            UpstreamUrl = "http://localhost:8080",
            IsEnabled = true,
        };

        configure?.Invoke(mapping);
        settings.ModelMappings.Add(mapping);
        return mapping;
    }

    private static JsonArray ParseMessages(string body) => (JsonArray)JsonNode.Parse(body)!["messages"]!;

    // ── Helper contract ──────────────────────────────────────────────────

    [Fact]
    public void NeedsRepair_FalseWhenEveryMessageHasContent()
    {
        JsonArray messages = ParseMessages("""
            {"messages":[
              {"role":"system","content":"s"},
              {"role":"user","content":[{"type":"text","text":"hi"}]},
              {"role":"assistant","content":"","tool_calls":[]}]}
            """);

        Assert.False(MessageContentRepair.NeedsRepair(messages));
    }

    [Fact]
    public void NeedsRepair_TrueForNullContent()
    {
        JsonArray messages = ParseMessages("""
            {"messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"c1"}]}]}
            """);

        Assert.True(MessageContentRepair.NeedsRepair(messages));
    }

    [Fact]
    public void NeedsRepair_TrueForAbsentContent()
    {
        JsonArray messages = ParseMessages("""
            {"messages":[{"role":"assistant","tool_calls":[{"id":"c1"}]}]}
            """);

        Assert.True(MessageContentRepair.NeedsRepair(messages));
    }

    [Fact]
    public void Repair_SuppliesEmptyContentAndLeavesEverythingElseAlone()
    {
        JsonArray messages = ParseMessages("""
            {"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function"}]},
              {"role":"tool","tool_call_id":"c1"}]}
            """);

        int repaired = MessageContentRepair.Repair(messages);

        Assert.Equal(2, repaired);
        Assert.Equal("hi", (string?)messages[0]!["content"]);
        Assert.Equal(string.Empty, (string?)messages[1]!["content"]);
        Assert.Equal("c1", (string?)messages[1]!["tool_calls"]![0]!["id"]);
        Assert.Equal(string.Empty, (string?)messages[2]!["content"]);
        Assert.Equal("c1", (string?)messages[2]!["tool_call_id"]);
    }

    [Fact]
    public void Repair_IsANoOpWhenNothingNeedsIt()
    {
        JsonArray messages = ParseMessages("""
            {"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":null}]}
            """);

        // Repair once, then confirm a second pass reports nothing to do — the pipeline relies on the
        // count to decide whether the body counts as rewritten.
        Assert.Equal(1, MessageContentRepair.Repair(messages));
        Assert.Equal(0, MessageContentRepair.Repair(messages));
        Assert.False(MessageContentRepair.NeedsRepair(messages));
    }

    [Fact]
    public void WriteRepaired_SubstitutesEmptyStringForNullAndKeepsSiblingMembers()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"role":"assistant","content":null,"tool_calls":[{"id":"c1"}],"extra":7}
            """);

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            MessageContentRepair.WriteRepaired(writer, doc.RootElement);
        }

        JsonNode result = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!;
        Assert.Equal(string.Empty, (string?)result["content"]);
        Assert.Equal("c1", (string?)result["tool_calls"]![0]!["id"]);
        Assert.Equal(7, (int?)result["extra"]);
    }

    [Fact]
    public void WriteRepaired_AppendsContentWhenAbsent()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"role":"assistant","tool_calls":[{"id":"c1"}]}""");

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            MessageContentRepair.WriteRepaired(writer, doc.RootElement);
        }

        JsonNode result = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!;
        Assert.True(result.AsObject().ContainsKey("content"));
        Assert.Equal(string.Empty, (string?)result["content"]);
    }

    // ── Legacy passthrough path ──────────────────────────────────────────

    [Fact]
    public void NormalizeRequestBody_RepairsNullContentOnAssistantToolCallTurn()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = """
            {"model":"m","messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"f","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":"sunny"}]}
            """;

        string result = OllamaProxyHandler.NormalizeRequestBody(body, settings, new RequestLog());

        JsonNode messages = JsonNode.Parse(result)!["messages"]!;
        Assert.Equal(string.Empty, (string?)messages[1]!["content"]);
        Assert.Equal("c1", (string?)messages[1]!["tool_calls"]![0]!["id"]);
        Assert.Equal("weather?", (string?)messages[0]!["content"]);
        Assert.Equal("sunny", (string?)messages[2]!["content"]);
    }

    [Fact]
    public void NormalizeRequestBody_RepairsAbsentContent()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = """
            {"model":"m","messages":[
              {"role":"user","content":"go"},
              {"role":"assistant","tool_calls":[{"id":"c1","type":"function"}]}]}
            """;

        string result = OllamaProxyHandler.NormalizeRequestBody(body, settings, new RequestLog());

        JsonNode messages = JsonNode.Parse(result)!["messages"]!;
        Assert.True(messages[1]!.AsObject().ContainsKey("content"));
        Assert.Equal(string.Empty, (string?)messages[1]!["content"]);
    }

    [Fact]
    public void NormalizeRequestBody_LeavesAConformingBodyByteIdentical()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.ModelName = "m");

        // No content problem, so nothing may be rewritten — unusual spacing must survive verbatim.
        string body = """
            { "model" : "m" , "messages" : [ {"role":"assistant","content":"","tool_calls":[{"id":"c1"}]}, {"role":"user","content":"hi"} ] }
            """;

        string result = OllamaProxyHandler.NormalizeRequestBody(body, settings, new RequestLog());

        Assert.Equal(body, result);
    }

    [Fact]
    public void NormalizeRequestBody_DebugModeRecordsTheContentRepair()
    {
        AppSettings settings = SettingsWith(s => s.DebugMode = true);
        AddMapping(settings, "m");

        string body = """
            {"model":"m","messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"c1"}]}]}
            """;

        RequestLog log = new();
        OllamaProxyHandler.NormalizeRequestBody(body, settings, log);

        Assert.Contains("added an empty content field", log.DebugSummary);
    }

    // ── IR pipeline path ─────────────────────────────────────────────────

    [Fact]
    public void Pipeline_RepairsNullContentAndReportsIt()
    {
        AppSettings settings = SettingsWith(s => s.DebugMode = true);
        AddMapping(settings, "m");

        string body = """
            {"model":"m","messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"c1"}]}]}
            """;

        RequestLog log = new();
        RequestPayloadPipeline.Result result = RequestPayloadPipeline.Run(
            body, settings, log, RequestPayloadPipeline.DefaultSteps);

        JsonNode messages = JsonNode.Parse(result.Body)!["messages"]!;
        Assert.Equal(string.Empty, (string?)messages[0]!["content"]);
        Assert.Contains("added an empty content field", log.DebugSummary);
    }

    [Fact]
    public void Pipeline_StepDoesNotApplyToAConformingBody()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.ModelName = "m");

        RequestPayloadContext context = new()
        {
            OriginalJson = "{}",
            Payload = (JsonObject)JsonNode.Parse("""
                {"model":"m","messages":[{"role":"user","content":"hi"}]}
                """),
            Settings = settings,
            Log = new RequestLog(),
        };

        Assert.False(new MessageContentStep().Applies(context));
    }

    [Fact]
    public void Pipeline_AndLegacyAgreeOnTheRepairedBody()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = """
            {"model":"m","messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1"}]},
              {"role":"assistant","tool_calls":[{"id":"c2"}]}]}
            """;

        string legacy = OllamaProxyHandler.NormalizeRequestBody(body, settings, new RequestLog());
        RequestPayloadPipeline.Result pipeline = RequestPayloadPipeline.Run(
            body, settings, new RequestLog(), RequestPayloadPipeline.DefaultSteps);

        Assert.Equal(Canonical(legacy), Canonical(pipeline.Body));
    }

    private static string Canonical(string json) => Canonicalize(JsonNode.Parse(json)!);

    private static string Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonicalize(pair.Value!))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(item => Canonicalize(item!))) + "]",
        _ => node.ToJsonString(),
    };
}