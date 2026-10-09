using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies how <see cref="OllamaProxyHandler.NormalizeRequestBody"/> handles the OpenAI
/// <c>stream_options</c> block on the <c>/v1/*</c> passthrough path. Under Copilot compatibility the
/// block is stripped from the upstream request — many local OpenAI-compatible servers reject or ignore
/// it — and what the client asked for is reported back so the response side can synthesize the missing
/// terminal usage chunk.
/// </summary>
public class StreamOptionsNormalizationTests
{
    private static AppSettings CreateSettings(bool copilotCompatible = true, string upstreamModel = "upstream-model")
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "test-model",
            ModelName = upstreamModel,
            UpstreamUrl = "http://localhost:8080",
            EnableCopilotCompatibility = copilotCompatible,
        });
        return settings;
    }

    private static (JsonElement Root, StreamOptionsInfo StreamOptions) Normalize(string json, AppSettings settings)
    {
        RequestLog log = new();
        string result = OllamaProxyHandler.NormalizeRequestBody(
            json,
            settings,
            log,
            shouldApplyThinkingCompatibility: null,
            out StreamOptionsInfo streamOptions);
        return (JsonDocument.Parse(result).RootElement.Clone(), streamOptions);
    }

    [Fact]
    public void CopilotCompatibilityStripsStreamOptionsFromUpstreamBody()
    {
        (JsonElement root, _) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""",
            CreateSettings(copilotCompatible: true));

        Assert.False(root.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public void CopilotCompatibilityLeavesStreamAloneWhenStrippingStreamOptions()
    {
        (JsonElement root, _) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""",
            CreateSettings(copilotCompatible: true));

        // The client still wants an SSE stream; only the options block is removed.
        Assert.True(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void WhenCopilotCompatibilityDisabledThenStreamOptionsIsForwarded()
    {
        (JsonElement root, StreamOptionsInfo info) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""",
            CreateSettings(copilotCompatible: false));

        Assert.True(root.TryGetProperty("stream_options", out JsonElement options));
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.True(info.Present);
        Assert.False(info.Stripped);
    }

    [Fact]
    public void StrippedStreamOptionsReportsIncludeUsageForSynthesis()
    {
        (_, StreamOptionsInfo info) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""",
            CreateSettings(copilotCompatible: true));

        Assert.True(info.Present);
        Assert.True(info.IncludeUsage);
        Assert.True(info.Stripped);
        Assert.True(info.MustSynthesizeUsage);
    }

    [Fact]
    public void StrippedStreamOptionsWithoutIncludeUsageDoesNotRequireSynthesizedUsage()
    {
        (_, StreamOptionsInfo info) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":false}}""",
            CreateSettings(copilotCompatible: true));

        Assert.True(info.Present);
        Assert.False(info.IncludeUsage);
        Assert.True(info.Stripped);
        Assert.False(info.MustSynthesizeUsage);
    }

    [Fact]
    public void BodyWithoutStreamOptionsReportsNoneAndIsLeftUntouched()
    {
        const string json = """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""";

        (JsonElement root, StreamOptionsInfo info) = Normalize(json, CreateSettings(copilotCompatible: true));

        Assert.Equal(StreamOptionsInfo.None, info);
        Assert.False(root.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public void StreamOptionsIsStrippedEvenWhenNothingElseNeedsRewriting()
    {
        // ResolveModelName maps ProxyName -> ModelName, so an identical pair leaves the model field
        // unchanged. With no sampling or reasoning overrides either, the stream_options strip is the
        // only pending rewrite — without it participating in the "nothing to rewrite" gate the method
        // would return the original text and forward the block untouched.
        const string json = """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream_options":{"include_usage":true}}""";

        (JsonElement root, StreamOptionsInfo info) = Normalize(
            json,
            CreateSettings(copilotCompatible: true, upstreamModel: "test-model"));

        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("stream_options", out _));
        Assert.True(info.Stripped);
    }

    [Fact]
    public void StreamOptionsMemberIsMatchedCaseInsensitively()
    {
        // JsonElement.TryGetProperty is case-sensitive, so a client that capitalizes the member would
        // otherwise slip through unstripped. The property loop and the reader must agree.
        (_, StreamOptionsInfo info) = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"Stream_Options":{"Include_Usage":true}}""",
            CreateSettings(copilotCompatible: true));

        Assert.True(info.Present);
        Assert.True(info.IncludeUsage);
        Assert.True(info.Stripped);
    }
}
