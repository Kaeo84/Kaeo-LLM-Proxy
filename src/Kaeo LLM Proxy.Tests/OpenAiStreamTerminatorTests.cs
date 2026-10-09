using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies that <see cref="OpenAiStreamTerminator"/> closes an OpenAI SSE stream correctly.
/// Microsoft.Extensions.AI clients (Visual Studio Copilot among them) await a terminal stream event
/// and block indefinitely without one, so the terminator must supply exactly what the upstream
/// omitted — and nothing when the upstream already delivered it.
/// </summary>
public class OpenAiStreamTerminatorTests
{
    private const string DoneFrame = "data: [DONE]\n\n";

    /// <summary>A non-terminal token delta carrying <c>usage: null</c>, as OpenAI sends under include_usage.</summary>
    private const string DeltaFrame =
        "data: {\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
        "\"model\":\"upstream-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}],\"usage\":null}";

    /// <summary>A terminal frame in which the upstream reported real usage.</summary>
    private const string UpstreamUsageFrame =
        "data: {\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
        "\"model\":\"upstream-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4,\"total_tokens\":7}}";

    private static JsonObject ParseFrame(string frame)
    {
        Assert.StartsWith("data: ", frame);
        Assert.EndsWith("\n\n", frame);

        string payload = frame["data: ".Length..^2];
        return Assert.IsType<JsonObject>(JsonNode.Parse(payload));
    }

    [Fact]
    public void WhenUpstreamSentDoneThenTerminatorAddsNothing()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        terminator.ObserveLine("data: [DONE]");

        Assert.True(terminator.DoneSeen);
        Assert.Empty(terminator.BuildTerminalFrames(promptTokens: 1, completionTokens: 2));
    }

    [Fact]
    public void WhenUsageRequestedAndNeverReportedThenTerminatorSynthesizesUsageChunkThenDone()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        terminator.ObserveLine(DeltaFrame);

        IReadOnlyList<string> frames = terminator.BuildTerminalFrames(promptTokens: 11, completionTokens: 22);

        Assert.Equal(2, frames.Count);
        Assert.Equal(DoneFrame, frames[1]);
    }

    [Fact]
    public void WhenUsageChunkIsSynthesizedThenItCarriesTheUpstreamIdentityAndTokenTotals()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        terminator.ObserveLine(DeltaFrame);

        JsonObject usageChunk = ParseFrame(terminator.BuildTerminalFrames(promptTokens: 11, completionTokens: 22)[0]);

        // Identity comes from the first upstream chunk so the invented frame is indistinguishable
        // from the ones already streamed, except that model names the model the client asked for.
        Assert.Equal("chatcmpl-9", usageChunk["id"]!.GetValue<string>());
        Assert.Equal(1700000000L, usageChunk["created"]!.GetValue<long>());
        Assert.Equal("test-model", usageChunk["model"]!.GetValue<string>());
        Assert.Equal("chat.completion.chunk", usageChunk["object"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(usageChunk["choices"]));

        JsonObject usage = Assert.IsType<JsonObject>(usageChunk["usage"]);
        Assert.Equal(11, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(22, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(33, usage["total_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void WhenUsageWasNotRequestedThenTerminatorAddsOnlyDone()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: false);

        terminator.ObserveLine(DeltaFrame);

        Assert.Equal(new[] { DoneFrame }, terminator.BuildTerminalFrames(promptTokens: 5, completionTokens: 5));
    }

    [Fact]
    public void WhenUpstreamReportedUsageThenTerminatorDoesNotSynthesizeASecondChunk()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        terminator.ObserveLine(UpstreamUsageFrame);

        Assert.True(terminator.UsageSeen);
        Assert.Equal(new[] { DoneFrame }, terminator.BuildTerminalFrames(promptTokens: 3, completionTokens: 4));
    }

    [Fact]
    public void WhenStreamEndedWithNoFramesAtAllThenTerminatorAddsOnlyDone()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: false);

        Assert.False(terminator.DoneSeen);
        Assert.Equal(new[] { DoneFrame }, terminator.BuildTerminalFrames(promptTokens: 0, completionTokens: 0));
    }

    [Fact]
    public void WhenNoUpstreamChunkWasParseableThenSynthesizedUsageStillCarriesGeneratedIdentity()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        terminator.ObserveLine("data: {not json}");

        JsonObject usageChunk = ParseFrame(terminator.BuildTerminalFrames(promptTokens: 1, completionTokens: 1)[0]);

        Assert.StartsWith("chatcmpl-kaeo-", usageChunk["id"]!.GetValue<string>());
        Assert.Equal("test-model", usageChunk["model"]!.GetValue<string>());
    }

    [Fact]
    public void WhenDoneArrivesSplitAcrossByteBoundariesThenFeedRecognizesIt()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: false);

        // The byte-oriented copy path forwards whatever the socket handed it, so SSE framing and
        // buffer boundaries are unrelated. A terminator that only matched whole lines would miss
        // this and append a second [DONE].
        terminator.Feed("data: {\"choices\":[]}\n\ndata: [DO");
        terminator.Feed("NE]\n\n");
        terminator.Flush();

        Assert.True(terminator.DoneSeen);
        Assert.Empty(terminator.BuildTerminalFrames(promptTokens: 0, completionTokens: 0));
    }

    [Fact]
    public void WhenKeepAliveCommentsArriveThenTheyDoNotCountAsTerminalFrames()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: false);

        terminator.ObserveLine(": kaeo-keep-alive");
        terminator.ObserveLine(string.Empty);

        Assert.False(terminator.DoneSeen);
        Assert.Equal(new[] { DoneFrame }, terminator.BuildTerminalFrames(promptTokens: 0, completionTokens: 0));
    }

    [Fact]
    public void WhenNegativeTokenCountsAreSuppliedThenSynthesizedUsageReportsZero()
    {
        OpenAiStreamTerminator terminator = new("test-model", includeUsage: true);

        JsonObject usageChunk = ParseFrame(terminator.BuildTerminalFrames(promptTokens: -5, completionTokens: -7)[0]);

        JsonObject usage = Assert.IsType<JsonObject>(usageChunk["usage"]);
        Assert.Equal(0, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(0, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(0, usage["total_tokens"]!.GetValue<int>());
    }
}
