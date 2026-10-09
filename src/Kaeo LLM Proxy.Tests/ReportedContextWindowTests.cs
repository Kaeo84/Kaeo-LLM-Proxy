using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the proxy recovers the context window an upstream actually loaded from its
/// <c>exceed_context_size_error</c> rejection.
/// </summary>
/// <remarks>
/// This matters because the mapping's configured Context Window is a declaration of intent, not a
/// measurement. When the declared value is larger than what the server really has (a server started
/// with a smaller context, or a value the operator has not reconciled), compaction sizes its work
/// against the declared figure, concludes an oversized body fits, and forwards it untouched — the
/// upstream then rejects it and nothing was summarized. Reading the reported figure back is what
/// lets the proxy correct for that instead of silently doing nothing.
/// </remarks>
public class ReportedContextWindowTests
{
    [Fact]
    public void ReadsNCtxFromAStructuredErrorObject()
    {
        // Shape emitted by llama.cpp.
        const string body =
            """{"error":{"code":400,"message":"request (330550 tokens) exceeds the available context size (256000 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":330550,"n_ctx":256000}}""";

        Assert.Equal(256000, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }

    [Fact]
    public void ReadsNCtxWhenItSitsAtTheRoot()
    {
        const string body = """{"n_ctx":131072,"message":"too long"}""";

        Assert.Equal(131072, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }

    [Fact]
    public void FallsBackToTheProseFigureWhenNoStructuredFieldIsPresent()
    {
        // A provider that only describes the limit in the message.
        const string body =
            """{"error":{"message":"This model's maximum context length is 128000 tokens"}}""";

        Assert.Equal(128000, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }

    [Fact]
    public void ReturnsZeroWhenTheBodyReportsNoWindow()
    {
        const string body = """{"error":{"message":"rate limit exceeded"}}""";

        Assert.Equal(0, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void ReturnsZeroForBodiesThatCannotBeParsed(string body)
    {
        // A malformed or non-JSON rejection must not throw on the error path.
        Assert.Equal(0, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }

    [Fact]
    public void IgnoresANonPositiveWindow()
    {
        const string body = """{"error":{"n_ctx":0,"message":"exceeds context size"}}""";

        Assert.Equal(0, OllamaProxyHandler.ExtractReportedContextWindow(body));
    }
}