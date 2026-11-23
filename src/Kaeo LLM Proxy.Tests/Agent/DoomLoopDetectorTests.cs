using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies doom-loop detection: identical consecutive failures trip at the threshold,
/// successes and different calls reset the count, and the failure heuristic matches the
/// runtime's error conventions without counting permission denials.
/// </summary>
public class DoomLoopDetectorTests
{
    [Fact]
    public void IdenticalFailures_TripAtThreshold()
    {
        var detector = new DoomLoopDetector(3);
        Assert.False(detector.Record("vs_build", "{}", failed: true));
        Assert.False(detector.Record("vs_build", "{}", failed: true));
        Assert.True(detector.Record("vs_build", "{}", failed: true));
    }

    [Fact]
    public void Success_ResetsCount()
    {
        var detector = new DoomLoopDetector(3);
        detector.Record("vs_build", "{}", failed: true);
        detector.Record("vs_build", "{}", failed: true);
        Assert.False(detector.Record("vs_build", "{}", failed: false));
        Assert.False(detector.Record("vs_build", "{}", failed: true));
        Assert.False(detector.Record("vs_build", "{}", failed: true));
        Assert.True(detector.Record("vs_build", "{}", failed: true));
    }

    [Fact]
    public void DifferentArguments_ResetCount()
    {
        var detector = new DoomLoopDetector(3);
        detector.Record("vs_build", "{\"a\":1}", failed: true);
        detector.Record("vs_build", "{\"a\":1}", failed: true);
        Assert.False(detector.Record("vs_build", "{\"a\":2}", failed: true));
        Assert.False(detector.Record("vs_build", "{\"a\":2}", failed: true));
        Assert.True(detector.Record("vs_build", "{\"a\":2}", failed: true));
    }

    [Fact]
    public void DifferentTool_ResetCount()
    {
        var detector = new DoomLoopDetector(3);
        detector.Record("vs_build", "{}", failed: true);
        detector.Record("vs_build", "{}", failed: true);
        Assert.False(detector.Record("vs_run_tests", "{}", failed: true));
    }

    [Fact]
    public void Threshold_ClampedToMinimumTwo()
    {
        var detector = new DoomLoopDetector(1);
        Assert.False(detector.Record("t", null, failed: true));
        Assert.True(detector.Record("t", null, failed: true));
    }

    [Theory]
    [InlineData("Tool execution failed: boom", true)]
    [InlineData("Unknown built-in tool: nope", true)]
    [InlineData("Tool 'x' not found in any enabled MCP server.", true)]
    [InlineData("git status failed (exit 128): fatal", true)]
    [InlineData("Checkpoint failed: git add failed", true)]
    [InlineData("Rollback failed: unknown id", true)]
    [InlineData("Permission denied by user.", false)]
    [InlineData("Build succeeded. 0 Errors", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FailureHeuristic(string? result, bool expected) =>
        Assert.Equal(expected, DoomLoopDetector.LooksLikeFailure(result));
}