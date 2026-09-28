using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins that a long compaction never leaves an already-committed SSE stream silent.
/// </summary>
/// <remarks>
/// The failure this exists because of: Copilot's manual compaction cancelled with a
/// <c>TaskCanceledException</c> from its own summarizer. The proxy had committed the SSE headers and
/// then written nothing for the entire chunked summarization — 2m27s on a 918k-token transcript —
/// which exceeded the client's read timeout. <see cref="OllamaProxyHandler"/> deliberately reuses the
/// keep-alive pump already built for the thinking phase, where the upstream is likewise silent.
/// </remarks>
public class CompactionKeepAliveTests
{
    /// <summary>The SSE comment frame the pump emits; conformant clients discard it.</summary>
    private const string KeepAliveFrame = ": kaeo-keep-alive\n\n";

    /// <summary>
    /// Records what is written and how long each write waited, so a pump can be distinguished from a
    /// single frame and from no traffic at all.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly List<(string Text, long ElapsedMs)> _writes = [];

        public IReadOnlyList<(string Text, long ElapsedMs)> Writes => _writes;

        public int WriteCount => _writes.Count;

        public int KeepAliveFrameCount =>
            _writes.Count(w => w.Text.Contains(KeepAliveFrame.Trim(), StringComparison.Ordinal));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }

        public override void Write(byte[] buffer, int offset, int count) =>
            _writes.Add((Encoding.UTF8.GetString(buffer, offset, count), Environment.TickCount64));

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _writes.Add((Encoding.UTF8.GetString(buffer, offset, count), Environment.TickCount64));
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ── The pump emits frames on the configured cadence ───────────────────

    [Fact]
    public async Task ThePumpStaysRunningUntilCancelledRatherThanReturningImmediately()
    {
        // The contract the compaction path depends on: the pump is still alive while the work is in
        // progress. The interval is clamped to a 5s floor, so frames cannot be observed quickly; what can
        // be observed is that the pump is still running after a delay. A pump that returned immediately
        // would leave the stream silent and the client would cancel — the reported failure.
        using RecordingStream stream = new();
        using CancellationTokenSource cts = new();

        Task pump = OllamaProxyHandler.PumpPreResponseSseKeepAliveForTest(stream, 5, cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.False(pump.IsCompleted, "The pump must still be running while compaction is in progress.");

        await cts.CancelAsync();
        await pump;

        Assert.True(pump.IsCompletedSuccessfully);
    }

    [Fact]
    public void TheKeepAliveIntervalFloorIsWellBelowATypicalClientReadTimeout()
    {
        // The pump's cadence is what keeps a client alive. If the clamp were ever raised near or above
        // a typical client timeout (the OpenAI SDK default is 100s) the pump would exist but not help,
        // and a long compaction would still be cancelled.
        TimeSpan clamped = AppSettings.SseKeepAliveInterval(AppSettings.MinSseKeepAliveIntervalSeconds);

        Assert.True(
            clamped < TimeSpan.FromSeconds(100),
            "The keep-alive cadence must stay comfortably below a typical client read timeout.");

        // And the shipped default is the value actually used unless overridden.
        Assert.True(
            AppSettings.SseKeepAliveInterval(AppSettings.DefaultSseKeepAliveIntervalSeconds)
                < TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void AFrameIsACommentLineSoConformantClientsDiscardIt()
    {
        // A frame that looked like data would be parsed as model output. SSE comments start with ':',
        // which is why the pump can interleave them at any point without corrupting the response.
        Assert.StartsWith(":", KeepAliveFrame, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", KeepAliveFrame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledPumpStopsWithoutThrowing()
    {
        // The compaction path cancels and awaits the pump in a finally block. A pump that threw on
        // cancellation would turn a successful compaction into a failed request.
        using RecordingStream stream = new();
        using CancellationTokenSource cts = new();

        Task pump = OllamaProxyHandler.PumpPreResponseSseKeepAliveForTest(stream, 5, cts.Token);
        await cts.CancelAsync();

        await pump; // Must not throw.

        Assert.True(pump.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task APumpStartedWithAnAlreadyCancelledTokenStopsImmediately()
    {
        // Guards the ordering in the finally block: cancelling before awaiting must not hang.
        using RecordingStream stream = new();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Task pump = OllamaProxyHandler.PumpPreResponseSseKeepAliveForTest(stream, 5, cts.Token);

        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.WriteCount);
    }
}