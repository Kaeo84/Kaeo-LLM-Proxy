using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins how token counts are recovered from an upstream that reports them as llama.cpp
/// <c>timings</c> instead of an OpenAI <c>usage</c> object.
/// </summary>
/// <remarks>
/// The failure these exist because of: Copilot compatibility strips
/// <c>stream_options.include_usage</c> from the upstream request, and llama.cpp only adds a
/// <c>usage</c> object when it is asked for one. Its terminal chunk still carries
/// <c>timings.prompt_n</c> / <c>timings.predicted_n</c>, but the sniffer ignored frames without a
/// <c>usage</c> member, so every llama.cpp-served model logged zero tokens and the dashboard's
/// Usage view attributed nothing to it — its whole week collapsed onto the one cloud model that
/// reported usage on its own.
/// </remarks>
public class UpstreamTokenCaptureTests
{
    private const string DeltaFrame =
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"hi\"}}]," +
        "\"created\":1795825321,\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\"}";

    private const string DoneFrame = "data: [DONE]";

    // Verbatim shape of a llama.cpp b12651 terminal chunk when stream_options was stripped.
    private static string TimingsFrame(int cacheN, int promptN, int predictedN, int draftN = 0, int draftNAccepted = 0) =>
        "data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"delta\":{}}]," +
        "\"created\":1795825321,\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\"," +
        "\"timings\":{" +
        $"\"cache_n\":{cacheN},\"prompt_n\":{promptN},\"prompt_ms\":283.284," +
        $"\"predicted_n\":{predictedN},\"predicted_ms\":90.291," +
        $"\"draft_n\":{draftN},\"draft_n_accepted\":{draftNAccepted}}}}}";

    private static string UsageAndTimingsFrame() =>
        "data: {\"choices\":[],\"created\":1795825324,\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\"," +
        "\"usage\":{\"completion_tokens\":8,\"prompt_tokens\":53,\"total_tokens\":61," +
        "\"prompt_tokens_details\":{\"cached_tokens\":49}}," +
        "\"timings\":{\"cache_n\":49,\"prompt_n\":4,\"predicted_n\":8}}";

    [Fact]
    public void TimingsAloneSupplyTokenCounts()
    {
        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest(
            [DeltaFrame, TimingsFrame(cacheN: 0, promptN: 53, predictedN: 8), DoneFrame]);

        Assert.Equal(53, log.PromptTokens);
        Assert.Equal(8, log.CompletionTokens);
        Assert.Equal(61, log.TotalTokens);
        Assert.Equal(0, log.CachedPromptTokens);
    }

    [Fact]
    public void CachedPromptTokensAreCountedBackIntoThePromptTotal()
    {
        // prompt_n counts only the tokens processed in the final batch, so a warm cache would
        // otherwise under-report the prompt by everything that was reused.
        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest(
            [DeltaFrame, TimingsFrame(cacheN: 49, promptN: 4, predictedN: 8), DoneFrame]);

        Assert.Equal(53, log.PromptTokens);
        Assert.Equal(8, log.CompletionTokens);
        Assert.Equal(61, log.TotalTokens);
        Assert.Equal(49, log.CachedPromptTokens);
    }

    [Fact]
    public void AReportedUsageObjectWinsOverTimings()
    {
        // An upstream that does answer include_usage must not have its exact numbers replaced by
        // the timings approximation.
        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest(
            [DeltaFrame, UsageAndTimingsFrame(), DoneFrame]);

        Assert.Equal(53, log.PromptTokens);
        Assert.Equal(8, log.CompletionTokens);
        Assert.Equal(61, log.TotalTokens);
        Assert.Equal(49, log.CachedPromptTokens);
    }

    [Fact]
    public void DraftCountersAreStillRecordedFromTimings()
    {
        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest(
            [DeltaFrame, TimingsFrame(cacheN: 0, promptN: 53, predictedN: 8, draftN: 5, draftNAccepted: 4), DoneFrame]);

        Assert.Equal(5, log.DraftN);
        Assert.Equal(4, log.DraftNAccepted);
    }

    [Fact]
    public void TokenDeltasAloneRecordNothing()
    {
        // The sniffer must not invent counts from ordinary content frames: a request the upstream
        // never totalled stays unattributed rather than being reported with made-up numbers.
        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest([DeltaFrame, DeltaFrame, DoneFrame]);

        Assert.Equal(0, log.PromptTokens);
        Assert.Equal(0, log.CompletionTokens);
        Assert.Equal(0, log.TotalTokens);
        Assert.Equal(0, log.CachedPromptTokens);
    }

    [Fact]
    public void AFinalFrameWithoutUsageOrTimingsRecordsNothing()
    {
        // llama.cpp also terminates with a finish_reason-only chunk when the client disconnected
        // before any prediction happened; there is nothing to count and nothing to invent.
        const string finishOnly =
            "data: {\"choices\":[{\"finish_reason\":\"length\",\"index\":0,\"delta\":{}}]," +
            "\"created\":1795825321,\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\"}";

        RequestLog log = OllamaProxyHandler.SniffUpstreamTokenCountsForTest([DeltaFrame, finishOnly, DoneFrame]);

        Assert.Equal(0, log.PromptTokens);
        Assert.Equal(0, log.CompletionTokens);
    }
}