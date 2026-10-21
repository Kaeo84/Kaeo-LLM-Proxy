using System.Net;
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
    /// <summary>
    /// Records what is written and how long each write waited, so a pump can be distinguished from a
    /// single frame and from no traffic at all.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly List<(string Text, long ElapsedMs)> _writes = [];

        public IReadOnlyList<(string Text, long ElapsedMs)> Writes => _writes;

        public int WriteCount => _writes.Count;

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

    /// <summary>
    /// Fails every write the way a closed listener socket does, counting the attempts so a pump that
    /// tried to write can be distinguished from one that never did.
    /// </summary>
    private sealed class DisconnectedClientStream : Stream
    {
        private readonly TaskCompletionSource _firstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WriteAttempts { get; private set; }

        /// <summary>
        /// Completes on the first write attempt so tests can await the pump's first frame instead of
        /// polling on a delay loop, which made pass/fail timing-dependent.
        /// </summary>
        public Task FirstWriteAttempted => _firstWrite.Task;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            _firstWrite.TrySetResult();
            throw Disconnected();
        }

        // The pump writes a ReadOnlyMemory, which the base class forwards to this overload because the
        // frame bytes are a plain array. That is what makes it throw for the same reason the real
        // HttpResponseStream does.
        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            _firstWrite.TrySetResult();
            throw Disconnected();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static HttpListenerException Disconnected() =>
            new(64, "An operation was attempted on a nonexistent network connection.");
    }

    // ── The pump emits frames on the configured cadence ───────────────────

    [Fact]
    public async Task ThePumpStaysRunningUntilCancelledRatherThanReturningImmediately()
    {
        // The contract the compaction path depends on: the pump is still alive while the work is in
        // progress. Driven on an explicit cadence longer than the observation window, so no frame is
        // due yet and the only thing being proved is that the pump has not returned. A pump that
        // returned immediately would leave the stream silent and the client would cancel — the
        // reported failure.
        using RecordingStream stream = new();
        using CancellationTokenSource cts = new();

        Task pump = OllamaProxyHandler.PumpPreResponseSseKeepAliveForTest(
            stream, TimeSpan.FromSeconds(30), cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.False(pump.IsCompleted, "The pump must still be running while compaction is in progress.");
        Assert.Equal(0, stream.WriteCount);

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
        //
        // Read from the handler rather than a copy of the literal in this file: the previous version
        // asserted against its own const, so it stayed green no matter what the proxy emitted.
        string frame = OllamaProxyHandler.SseKeepAliveFrameForTest;

        Assert.StartsWith(":", frame, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", frame, StringComparison.Ordinal);
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

    // ── A client disconnect is not an upstream error ──────────────────────

    [Fact]
    public void AClientDisconnectIsDescribedAsSuchRatherThanBlamingTheUpstream()
    {
        // HttpListenerException is the client closing the socket. It derives from Win32Exception, not
        // IOException, so it previously fell through to the generic arm and every client disconnect was
        // logged as an upstream error — which cost real diagnosis time on a reported failure.
        string description = OllamaProxyHandler.DescribePassthroughFailureForTest(
            new HttpListenerException(64, "An operation was attempted on a nonexistent network connection."));

        Assert.Contains("client disconnected", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upstream", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClientDisconnectIsRecognisedSoItCanBeLoggedQuietly()
    {
        // Copilot abandons a turn around compaction routinely, so this must not warn.
        Assert.True(OllamaProxyHandler.IsClientDisconnectForTest(
            new HttpListenerException(64, "An operation was attempted on a nonexistent network connection.")));

        // A genuine upstream failure must not be classified as a client disconnect, or real faults would
        // be downgraded to Debug and lost.
        Assert.False(OllamaProxyHandler.IsClientDisconnectForTest(
            new HttpRequestException("Connection refused (127.0.0.1:8080)")));
        Assert.False(OllamaProxyHandler.IsClientDisconnectForTest(new TimeoutException("slow")));
    }

    [Fact]
    public void ATimeoutIsStillNamedForTheClient()
    {
        // The cases a client can act on must survive the tightening, or the leak fix would have traded
        // one problem for another.
        Assert.Contains("timed out", OllamaProxyHandler.DescribePassthroughFailureForTest(new TimeoutException("x9")));
        Assert.Contains("timed out", OllamaProxyHandler.DescribePassthroughFailureForTest(new TaskCanceledException("x9")));
    }

    [Fact]
    public async Task AKeepAliveWriteToADisconnectedClientStopsThePumpWithoutThrowing()
    {
        // The compaction paths await this pump inside a finally block. Keep-alive is the only traffic on
        // the wire during a redirected compaction, so an abandoned client surfaces on its write as an
        // HttpListenerException; before that was tolerated the exception propagated out of the await,
        // skipping the compaction cancellation and the local-retry cleanup, and a routine disconnect was
        // reported as a passthrough failure.
        using DisconnectedClientStream stream = new();
        using CancellationTokenSource cts = new();

        // Driven on an explicit 20ms cadence rather than the clamped production interval. The 5s
        // floor means the first write — and therefore the failing one — could not land any sooner,
        // which made this single assertion cost five seconds of every test run.
        Task pump = OllamaProxyHandler.PumpPreResponseSseKeepAliveForTest(
            stream, TimeSpan.FromMilliseconds(20), cts.Token);

        await stream.FirstWriteAttempted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stream.WriteAttempts > 0, "The pump should have attempted a keep-alive write.");

        await pump.WaitAsync(TimeSpan.FromSeconds(5)); // Must not throw.

        Assert.True(pump.IsCompletedSuccessfully);
    }
}