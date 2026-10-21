using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services;

/// <summary>Identifies the wire protocol of a live streaming response.</summary>
internal enum StreamKind
{
    /// <summary>OpenAI-style Server-Sent Events (<c>text/event-stream</c>) forwarded to the client.</summary>
    Sse,

    /// <summary>Ollama NDJSON streams produced by <c>/api/chat</c> and <c>/api/generate</c>.</summary>
    Ollama,
}

/// <summary>Coarse lifecycle state of a live stream, computed for the dashboard.</summary>
internal enum StreamState
{
    /// <summary>Headers committed, but no upstream frame has arrived yet.</summary>
    Connecting,

    /// <summary>Upstream frames are arriving.</summary>
    Streaming,

    /// <summary>Was streaming, but the upstream has since gone quiet (only keep-alive frames).</summary>
    Waiting,
}

/// <summary>
/// Immutable point-in-time view of one live stream, safe to hand to the UI thread without
/// sharing mutable state with the request pipeline.
/// </summary>
internal sealed class StreamSnapshot
{
    public string RequestId { get; set; } = string.Empty;
    public StreamKind Kind { get; set; }
    public StreamState State { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime LastUpstreamActivityUtc { get; set; }
    public DateTime LastClientActivityUtc { get; set; }
    public long UpstreamBytes { get; set; }
    public long ClientBytes { get; set; }
    public long KeepAliveFrames { get; set; }
    public int FrameCount { get; set; }

    /// <summary>Bounded rolling tail of the raw upstream (pre-rewrite) frames.</summary>
    public string RawUpstreamText { get; set; } = string.Empty;

    /// <summary>Bounded rolling tail of the frames forwarded to the client.</summary>
    public string ForwardedText { get; set; } = string.Empty;

    /// <summary>
    /// When the stream ended and was moved to history. <see cref="DateTime.MinValue"/>
    /// for live streams. History entries carry a real timestamp.
    /// </summary>
    public DateTime EndedUtc { get; set; } = DateTime.MinValue;
}

/// <summary>
/// Tracks the streaming responses currently in flight so the dashboard can show, live, whether
/// each one is still connecting, whether data is flowing, and what is on the wire. Streams are
/// registered by <see cref="Begin"/> and removed the moment their session is disposed, so the list
/// only ever shows open streams.
/// </summary>
/// <remarks>
/// Every member is safe to call from any thread. The registry itself is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and each session guards only its own counters and
/// buffers, so a chatty stream never contends with the shared registry.
/// </remarks>
internal sealed class StreamMonitor
{
    /// <summary>
    /// Number of characters retained per live buffer (raw upstream and forwarded). A stream that
    /// outlives this window keeps only its most recent frames, so a very long generation cannot grow
    /// memory without bound.
    /// </summary>
    public const int MaxLiveChars = 32 * 1024;

    private readonly ConcurrentDictionary<string, StreamMonitorSession> _sessions = new(StringComparer.Ordinal);

        /// <summary>Retention window for completed-stream history (in-memory only, no persistence).</summary>
        private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(15);

        private readonly object _historyLock = new();
        private readonly List<StreamSnapshot> _history = [];

        /// <summary>
        /// Raised when a stream is registered, removed, or moved to history.
        /// Deliberately not raised per frame — the UI polls <see cref="Snapshot"/>
        /// and <see cref="RecentHistory"/> on its refresh timer for the byte/live-data
        /// updates, so this event only needs to signal membership changes.
        /// </summary>
        public event EventHandler? StreamsChanged;

    /// <summary>Number of streams currently in flight.</summary>
    public int ActiveCount => _sessions.Count;

    /// <summary>
    /// Registers a new live stream and returns the session used to report its activity. Dispose the
    /// session to remove it from the registry.
    /// </summary>
    public StreamMonitorSession Begin(RequestLog log, StreamKind kind)
    {
        ArgumentNullException.ThrowIfNull(log);

        StreamMonitorSession session = new(this, log, kind, MaxLiveChars);
        _sessions[session.RequestId] = session;
        RaiseStreamsChanged();
        return session;
    }

    /// <summary>Returns a stable, ordered copy of the current streams for rendering.</summary>
    public IReadOnlyList<StreamSnapshot> Snapshot()
    {
        if (_sessions.IsEmpty)
            return [];

        List<StreamSnapshot> rows = new(_sessions.Count);
        foreach (StreamMonitorSession session in _sessions.Values)
            rows.Add(session.ToSnapshot());

        rows.Sort(static (a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
        return rows;
    }

    /// <summary>Removes a session, but only if it is still the registered instance for its id.</summary>
    internal void Remove(StreamMonitorSession session)
    {
        if (!_sessions.TryRemove(new KeyValuePair<string, StreamMonitorSession>(session.RequestId, session)))
            return;

        // Capture a final snapshot for the short in-memory history before the session is gone.
        StreamSnapshot final = session.ToSnapshot();
        final.EndedUtc = DateTime.UtcNow;
        AddToHistory(final);

        RaiseStreamsChanged();
    }

    /// <summary>
    /// Returns a stable, ordered copy of streams that completed within the last
    /// <see cref="HistoryWindow"/>, newest first. Prunes expired entries on each call
    /// so memory never grows without bound even if the UI stops polling.
    /// </summary>
    public IReadOnlyList<StreamSnapshot> RecentHistory()
    {
        DateTime cutoff = DateTime.UtcNow - HistoryWindow;

        lock (_historyLock)
        {
            _history.RemoveAll(s => s.EndedUtc < cutoff);
            return [.. _history];
        }
    }

    private void AddToHistory(StreamSnapshot entry)
    {
        lock (_historyLock)
        {
            _history.Add(entry);
        }
    }

    private void RaiseStreamsChanged()
    {
        try
        {
            StreamsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A UI handler must never fault the request pipeline that triggered the change.
        }
    }
}

/// <summary>
/// Per-stream reporting handle. Records byte counts, keep-alive frames, and bounded rolling tails of
/// the raw upstream and forwarded text; disposing removes the stream from its
/// <see cref="StreamMonitor"/>.
/// </summary>
internal sealed class StreamMonitorSession : IDisposable
{
    /// <summary>
    /// The upstream is considered "quiet" (state <see cref="StreamState.Waiting"/>) once this long has
    /// passed since the last upstream frame, which is what lets a user distinguish a slow model from a
    /// stalled one at a glance.
    /// </summary>
    private static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(3);

    private readonly StreamMonitor _owner;
    private readonly int _maxLiveChars;
    private readonly int _trimAtChars;
    private readonly object _gate = new();

    private readonly StringBuilder _rawUpstream = new();
    private readonly StringBuilder _forwarded = new();

    private long _upstreamBytes;
    private long _clientBytes;
    private long _keepAliveFrames;
    private int _frameCount;
    private DateTime _lastUpstreamActivityUtc;
    private DateTime _lastClientActivityUtc;
    private bool _disposed;

    internal StreamMonitorSession(StreamMonitor owner, RequestLog log, StreamKind kind, int maxLiveChars)
    {
        _owner = owner;
        _maxLiveChars = maxLiveChars;
        _trimAtChars = maxLiveChars + (maxLiveChars / 2);

        RequestId = string.IsNullOrEmpty(log.RequestId)
            ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            : log.RequestId;
        Kind = kind;
        Method = log.Method ?? string.Empty;
        Path = log.OllamaPath ?? string.Empty;
        Model = string.IsNullOrEmpty(log.OriginalModel) ? log.Model ?? string.Empty : log.OriginalModel;

        StartedUtc = DateTime.UtcNow;
        _lastUpstreamActivityUtc = StartedUtc;
        _lastClientActivityUtc = StartedUtc;
    }

    public string RequestId { get; }
    public StreamKind Kind { get; }
    public string Method { get; }
    public string Path { get; }
    public string Model { get; }
    public DateTime StartedUtc { get; }

    /// <summary>Records an upstream frame whose raw text is known (SSE rewrite and NDJSON paths).</summary>
    public void OnUpstreamLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            AppendBounded(_rawUpstream, line);
            AppendBounded(_rawUpstream, "\n");
            _upstreamBytes += Encoding.UTF8.GetByteCount(line) + 1;
            _lastUpstreamActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records an upstream chunk whose raw text and exact byte count are both known.</summary>
    public void OnUpstreamChunk(string text, int byteCount)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (!string.IsNullOrEmpty(text))
                AppendBounded(_rawUpstream, text);
            _upstreamBytes += byteCount;
            _lastUpstreamActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records a frame forwarded to the client, appending it to the forwarded tail.</summary>
    public void OnForwarded(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            AppendBounded(_forwarded, text);
            _clientBytes += Encoding.UTF8.GetByteCount(text);
            _frameCount++;
            _lastClientActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records byte-only client activity where the text is not separately tracked.</summary>
    public void OnClientBytes(int byteCount)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _clientBytes += byteCount;
            _lastClientActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records a keep-alive comment frame written to the client while the upstream is quiet.</summary>
    public void OnKeepAlive()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _keepAliveFrames++;
            _lastClientActivityUtc = DateTime.UtcNow;
        }
    }

    public StreamSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            return new StreamSnapshot
            {
                RequestId = RequestId,
                Kind = Kind,
                State = ComputeState(now),
                Method = Method,
                Path = Path,
                Model = Model,
                StartedUtc = StartedUtc,
                LastUpstreamActivityUtc = _lastUpstreamActivityUtc,
                LastClientActivityUtc = _lastClientActivityUtc,
                UpstreamBytes = _upstreamBytes,
                ClientBytes = _clientBytes,
                KeepAliveFrames = _keepAliveFrames,
                FrameCount = _frameCount,
                RawUpstreamText = _rawUpstream.ToString(),
                ForwardedText = _forwarded.ToString(),
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _owner.Remove(this);
    }

    private StreamState ComputeState(DateTime now)
    {
        if (_upstreamBytes == 0)
            return StreamState.Connecting;

        return now - _lastUpstreamActivityUtc > WaitingAfter
            ? StreamState.Waiting
            : StreamState.Streaming;
    }

    private void AppendBounded(StringBuilder buffer, string text)
    {
        buffer.Append(text);

        // Trims to the tail, never the head: even when a single append overshoots the window,
        // the most recent _maxLiveChars characters survive, so fresh frames are never dropped
        // in favour of older ones. Trimming lazily (at _trimAtChars) amortises the Remove over
        // many small appends instead of paying it on every frame.
        if (buffer.Length > _trimAtChars)
            buffer.Remove(0, buffer.Length - _maxLiveChars);
    }
}