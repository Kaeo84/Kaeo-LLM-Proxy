using Kaeo.LlmProxy.VSExtension.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies map-reduce compaction: token-boundary chunking without splitting messages,
/// the map-then-merge call sequence, transcript rendering of tool traffic, and cancellation.
/// </summary>
public class HierarchicalCompactorTests
{
    private static ChatMessage Text(string role, int chars) =>
        new(role == "user" ? ChatRole.User : ChatRole.Assistant, new string('x', chars));

    [Fact]
    public void Chunk_EmptyHistory_NoChunks()
    {
        Assert.Empty(HierarchicalCompactor.Chunk([]));
    }

    [Fact]
    public void Chunk_SmallHistory_SingleChunk()
    {
        var messages = new List<ChatMessage> { Text("user", 100), Text("assistant", 100) };
        var chunks = HierarchicalCompactor.Chunk(messages);
        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Count);
    }

    [Fact]
    public void Chunk_SplitsAtTokenBoundary()
    {
        // 400 chars = 100 tokens each; cap 250 → two messages per chunk.
        var messages = Enumerable.Range(0, 5).Select(_ => Text("user", 400)).ToList();
        var chunks = HierarchicalCompactor.Chunk(messages, chunkTokens: 250);
        Assert.Equal([2, 2, 1], chunks.Select(c => c.Count));
    }

    [Fact]
    public void Chunk_OversizedMessage_GetsOwnSegment()
    {
        var messages = new List<ChatMessage> { Text("user", 40), Text("assistant", 4000), Text("user", 40) };
        var chunks = HierarchicalCompactor.Chunk(messages, chunkTokens: 100);
        Assert.Equal(3, chunks.Count);
        Assert.Single(chunks[1]);
        Assert.Same(messages[1], chunks[1][0]);
    }

    [Fact]
    public void Chunk_PreservesAllMessagesInOrder()
    {
        var messages = Enumerable.Range(0, 20).Select(i => Text(i % 2 == 0 ? "user" : "assistant", 300 + i * 50)).ToList();
        var chunks = HierarchicalCompactor.Chunk(messages, chunkTokens: 200);
        Assert.Equal(messages, chunks.SelectMany(c => c));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Chunk_NonPositiveCap_Throws(int cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HierarchicalCompactor.Chunk([], cap));
    }

    [Fact]
    public void EstimateTokens_MinimumOneForEmptyMessage()
    {
        Assert.Equal(1, HierarchicalCompactor.EstimateTokens(new ChatMessage(ChatRole.User, "")));
    }

    [Fact]
    public void EstimateTokens_CountsToolCallsAndResults()
    {
        var call = new FunctionCallContent("id1", "vs_build", new Dictionary<string, object?> { ["project"] = "App.csproj" });
        var result = new FunctionResultContent("id1", new string('r', 400));
        var msg = new ChatMessage(ChatRole.Tool, [call, result]);
        // name (8) + serialized args (>10) + 400 result chars → well over 100 tokens.
        Assert.True(HierarchicalCompactor.EstimateTokens(msg) >= 100);
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmptyWithoutCalls()
    {
        var calls = 0;
        var summary = await HierarchicalCompactor.CompactAsync([], (_, _) => { calls++; return Task.FromResult("s"); });
        Assert.Equal("", summary);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CompactAsync_SingleSegment_OneSummarizeCall()
    {
        var prompts = new List<string>();
        var history = new List<ChatMessage> { new(ChatRole.User, "decided to use System.Text.Json") };
        var summary = await HierarchicalCompactor.CompactAsync(history, (p, _) =>
        {
            prompts.Add(p);
            return Task.FromResult("summary");
        });

        Assert.Equal("summary", summary);
        Assert.Single(prompts);
        Assert.Contains("segment 1 of 1", prompts[0]);
        Assert.Contains("decided to use System.Text.Json", prompts[0]);
    }

    [Fact]
    public async Task CompactAsync_MultiSegment_MapsThenMerges()
    {
        // 6 × 100-token messages, cap 250 → 3 segments → 3 map calls + 1 merge call.
        var history = Enumerable.Range(0, 6).Select(_ => Text("user", 400)).ToList();
        var prompts = new List<string>();
        var progress = new List<string>();
        var summary = await HierarchicalCompactor.CompactAsync(history, (p, _) =>
        {
            prompts.Add(p);
            return Task.FromResult($"S{prompts.Count}");
        }, chunkTokens: 250, progress: progress.Add);

        Assert.Equal(4, prompts.Count);
        Assert.Contains("segment 1 of 3", prompts[0]);
        Assert.Contains("segment 2 of 3", prompts[1]);
        Assert.Contains("segment 3 of 3", prompts[2]);
        Assert.Contains("Merge these 3", prompts[3]);
        Assert.Contains("S1", prompts[3]);
        Assert.Contains("S2", prompts[3]);
        Assert.Contains("S3", prompts[3]);
        Assert.Equal("S4", summary);
        Assert.Equal(4, progress.Count);
        Assert.Contains("merging", progress[3]);
    }

    [Fact]
    public async Task CompactAsync_PreCancelled_ThrowsBeforeAnyCall()
    {
        var history = Enumerable.Range(0, 6).Select(_ => Text("user", 400)).ToList();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HierarchicalCompactor.CompactAsync(history, (_, _) => { calls++; return Task.FromResult("s"); },
                chunkTokens: 250, ct: cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RenderTranscript_IncludesToolTraffic()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "fix the build"),
            new(ChatRole.Assistant, [new FunctionCallContent("id1", "vs_build", new Dictionary<string, object?> { ["project"] = "App.csproj" })]),
            new(ChatRole.Tool, [new FunctionResultContent("id1", "Build succeeded. 0 Errors")]),
        };
        var text = HierarchicalCompactor.RenderTranscript(messages);
        Assert.Contains("User: fix the build", text);
        Assert.Contains("[tool call vs_build", text);
        Assert.Contains("App.csproj", text);
        Assert.Contains("Tool result: Build succeeded. 0 Errors", text);
    }

    [Fact]
    public async Task CompactAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HierarchicalCompactor.CompactAsync(null!, (_, _) => Task.FromResult("s")));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HierarchicalCompactor.CompactAsync([], null!));
    }
}
