using Kaeo.LlmProxy.Core.Modules;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>Status snapshot of one configured channel for chat_channels and the config UI.</summary>
internal sealed record ChatChannelStatus(
    string Name,
    string ProviderType,
    string Target,
    bool Enabled,
    bool CanReceive,
    string? LastError,
    DateTime? LastPollUtc);

/// <summary>
/// The heart of the Chat Interface module: sends messages to configured chat channels, runs
/// one background poll loop that receives replies into bounded per-channel ring buffers, and
/// completes pending chat_ask waiters when a matching reply arrives. All per-channel state is
/// thread-safe; one failing channel never kills the loop; stop is clean and re-entrant-safe.
/// </summary>
internal sealed class ChatBridge
{
    private const int BufferCap = 200;
    private const int MaxBackoffSeconds = 300;

    private readonly ChatRepository _repository;
    private readonly ChatActivityLogger _activity;
    private readonly ChatProviderFactory _factory;
    private readonly ConcurrentDictionary<int, ChannelState> _states = new();
    private readonly object _lifecycleLock = new();
    private int _running;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ChatBridge(ChatRepository repository, ISecretProvider secrets, ChatActivityLogger activity)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _factory = new ChatProviderFactory(secrets);
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Starts the single background poll loop. Safe to call when already running.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running == 1)
                return;

            // A previous loop that died on its own leaves its CTS behind; reclaim it.
            _cts?.Dispose();

            CancellationTokenSource cts = new();
            _cts = cts;
            Volatile.Write(ref _running, 1);
            _loopTask = Task.Run(() => PollLoopAsync(cts.Token));
        }
    }

    /// <summary>Cancels and awaits the poll loop (bounded), then clears waiters. Safe when not running.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycleLock)
        {
            if (_running == 0)
                return;

            Volatile.Write(ref _running, 0);
            cts = _cts;
            loop = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (loop is not null)
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            cts.Dispose();
        }

        foreach (ChannelState state in _states.Values)
        {
            lock (state.Sync)
            {
                foreach (AskWaiter waiter in state.Waiters)
                    waiter.Completion.TrySetCanceled();
                state.Waiters.Clear();
            }
        }
    }

    // ── Poll loop ───────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<ChatChannelConfig> channels;
                try
                {
                    channels = _repository.LoadChannels();
                }
                catch (Exception ex) when (ex is IOException or SqliteException)
                {
                    // Sharing violation with another app instance: degrade and retry later.
                    Log.Warning(ex, "Chat poll loop could not read channels from the database");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                DateTime now = DateTime.UtcNow;
                foreach (ChatChannelConfig channel in channels)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    if (!channel.Enabled)
                        continue;

                    IChatProvider? provider = _factory.FindProvider(channel.ProviderType);
                    if (provider is null || !provider.CanReceive(channel))
                        continue;

                    ChannelState state = _states.GetOrAdd(channel.Id, _ => new ChannelState());
                    if (state.NextPollUtc > now)
                        continue;

                    // Per-channel isolation: one bad channel never kills the loop.
                    try
                    {
                        await PollChannelAsync(provider, channel, state, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        state.ConsecutiveErrors++;
                        state.LastError = ex.Message;
                        double backoff = Math.Min(MaxBackoffSeconds,
                            Math.Max(channel.PollIntervalSeconds, 15) * Math.Pow(2, Math.Min(state.ConsecutiveErrors, 6)));
                        state.NextPollUtc = DateTime.UtcNow.AddSeconds(backoff);

                        Log.Warning(ex, "Chat poll failed for channel {Channel}", channel.Name);
                        _activity.Write(new McpActivityEntry(ChatActivityLogger.Source, "poll")
                        {
                            Target = channel.Name,
                            IsError = true,
                            ErrorMessage = ex.Message,
                        });
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chat poll loop terminated unexpectedly");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task PollChannelAsync(IChatProvider provider, ChatChannelConfig channel, ChannelState state, CancellationToken cancellationToken)
    {
        // Skip when another poll of this channel is already in flight (e.g. chat_read's fresh poll).
        if (!await state.PollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            IReadOnlyList<ChatIncomingMessage> messages = await provider
                .PollAsync(channel, state.Cursor, cancellationToken).ConfigureAwait(false);
            string? nextCursor = provider.GetNextCursor(channel, messages, state.Cursor);

            lock (state.Sync)
            {
                state.Cursor = nextCursor;
                foreach (ChatIncomingMessage message in messages)
                {
                    if (!state.BufferIds.Add(message.Id))
                        continue; // duplicate (provider overlap or re-echo)

                    state.Buffer.Enqueue(message);
                    if (state.Buffer.Count > BufferCap)
                    {
                        ChatIncomingMessage evicted = state.Buffer.Dequeue();
                        state.BufferIds.Remove(evicted.Id);
                    }

                    if (!message.AuthorIsBot)
                        CompleteMatchingWaiter(state, message);
                }
            }

            state.LastPollUtc = DateTime.UtcNow;
            state.LastError = null;
            state.ConsecutiveErrors = 0;
            state.NextPollUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(channel.PollIntervalSeconds, 5, 300));

            if (messages.Count > 0)
            {
                _activity.WriteAtConnectivity(new McpActivityEntry(ChatActivityLogger.Source, "receive")
                {
                    Target = channel.Name,
                    ResponseDetail = _activity.FullEnabled ? $"{messages.Count} new message(s)." : null,
                });
            }
        }
        finally
        {
            state.PollGate.Release();
        }
    }

    /// <summary>
    /// Completes the oldest pending ask waiter this message answers: either an explicit reply
    /// to the question, or the first human message posted after the question was sent (with a
    /// small clock-skew tolerance). Called under <see cref="ChannelState.Sync"/>.
    /// </summary>
    private static void CompleteMatchingWaiter(ChannelState state, ChatIncomingMessage message)
    {
        for (int i = 0; i < state.Waiters.Count; i++)
        {
            AskWaiter waiter = state.Waiters[i];
            bool explicitReply = waiter.QuestionMessageId.Length > 0 && message.ReplyToId == waiter.QuestionMessageId;
            bool afterQuestion = message.TimestampUtc >= waiter.SentUtc.AddSeconds(-5);

            if (explicitReply || afterQuestion)
            {
                state.Waiters.RemoveAt(i);
                waiter.Completion.TrySetResult(message);
                return; // one waiter per message
            }
        }
    }

    // ── Tool-facing operations ──────────────────────────────────────────────

    /// <summary>Sends a message to a channel by name and returns a confirmation.</summary>
    public async Task<string> SendAsync(string channelName, string text, CancellationToken cancellationToken)
    {
        (ChatChannelConfig channel, string sentId) = await SendCoreAsync(channelName, text, cancellationToken).ConfigureAwait(false);
        return $"Message sent to channel '{channel.Name}' ({channel.ProviderType})." +
            (sentId.Length > 0 ? $" Message id: {sentId}." : string.Empty);
    }

    /// <summary>
    /// Sends a question and waits up to <paramref name="timeoutSeconds"/> (clamped 10..3600)
    /// for the user's reply. Returns the framed reply, or a no-reply notice on timeout.
    /// </summary>
    public async Task<string> AskAsync(string channelName, string question, int timeoutSeconds, CancellationToken cancellationToken)
    {
        int timeout = Math.Clamp(timeoutSeconds, 10, 3_600);
        ChatChannelConfig channel = ResolveChannel(channelName);
        IChatProvider provider = _factory.GetProvider(channel);

        if (!provider.CanReceive(channel))
            return $"Channel '{channel.Name}' ({channel.ProviderType}) is send-only as configured; " +
                "it cannot receive replies. Use chat_send instead, or configure receive support " +
                "in the Chat Interface settings.";
        if (!IsRunning)
            return "Chat polling is not running, so replies cannot be received. Start it on the " +
                "Chat Interface settings page (or enable the module) and ask again.";

        ChatSettings settings = _repository.LoadSettings();
        ChannelState state = _states.GetOrAdd(channel.Id, _ => new ChannelState());

        DateTime sentUtc = DateTime.UtcNow;
        (_, string sentId) = await SendCoreAsync(channelName, question, cancellationToken).ConfigureAwait(false);

        AskWaiter waiter = new()
        {
            QuestionMessageId = sentId,
            SentUtc = sentUtc,
            Completion = new TaskCompletionSource<ChatIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        lock (state.Sync)
        {
            state.Waiters.Add(waiter);
            // A reply may already have been polled between the send and the registration.
            foreach (ChatIncomingMessage buffered in state.Buffer)
            {
                if (!buffered.AuthorIsBot)
                    CompleteMatchingWaiter(state, buffered);
                if (waiter.Completion.Task.IsCompleted)
                    break;
            }
        }

        try
        {
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task completed = await Task.WhenAny(
                waiter.Completion.Task,
                Task.Delay(TimeSpan.FromSeconds(timeout), linkedCts.Token)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (completed == waiter.Completion.Task)
            {
                ChatIncomingMessage reply = await waiter.Completion.Task.ConfigureAwait(false);
                _activity.WriteAtConnectivity(new McpActivityEntry(ChatActivityLogger.Source, "ask-reply")
                {
                    Target = channel.Name,
                    ResponseDetail = _activity.FullEnabled ? $"{reply.Author}: {reply.Text}" : null,
                });

                string capped = ChatContentFraming.CapMessage(reply.Text, settings.MaxMessageChars);
                return $"Reply from {reply.Author} at {reply.TimestampUtc:u}:\n" +
                    ChatContentFraming.FrameUntrusted(capped);
            }

            _activity.Write(new McpActivityEntry(ChatActivityLogger.Source, "ask-timeout")
            {
                Target = channel.Name,
                IsCancelled = true,
                ErrorMessage = $"No reply received within {timeout} seconds.",
            });
            return $"No reply received within {timeout}s. The question was delivered to '{channel.Name}'; " +
                "use chat_read later to check for a late reply.";
        }
        finally
        {
            lock (state.Sync)
                state.Waiters.Remove(waiter);
        }
    }

    /// <summary>
    /// Returns recent buffered messages for a channel (optionally refreshed by one live poll),
    /// framed as untrusted content.
    /// </summary>
    public async Task<string> ReadAsync(string channelName, int sinceMinutes, int limit, CancellationToken cancellationToken)
    {
        int minutes = Math.Clamp(sinceMinutes, 1, 10_080);
        int count = Math.Clamp(limit, 1, 100);

        ChatChannelConfig channel = ResolveChannel(channelName);
        IChatProvider provider = _factory.GetProvider(channel);
        ChannelState state = _states.GetOrAdd(channel.Id, _ => new ChannelState());
        ChatSettings settings = _repository.LoadSettings();

        // One best-effort fresh poll so a read right after a reply does not lag the loop.
        if (channel.Enabled && provider.CanReceive(channel))
        {
            try
            {
                using CancellationTokenSource freshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                freshCts.CancelAfter(TimeSpan.FromSeconds(15));
                await PollChannelAsync(provider, channel, state, freshCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Chat fresh poll for channel {Channel} failed; serving buffered messages", channel.Name);
            }
        }

        DateTime cutoff = DateTime.UtcNow.AddMinutes(-minutes);
        List<ChatIncomingMessage> snapshot;
        lock (state.Sync)
        {
            snapshot = [.. state.Buffer.Where(m => m.TimestampUtc >= cutoff).TakeLast(count)];
        }

        if (snapshot.Count == 0)
            return $"No messages received on channel '{channel.Name}' in the last {minutes} minute(s). " +
                "Messages are only captured while polling is running.";

        StringBuilder output = new();
        output.AppendLine($"Recent messages on channel '{channel.Name}' ({snapshot.Count}):");
        foreach (ChatIncomingMessage message in snapshot)
        {
            output.AppendLine($"[{message.TimestampUtc:u}] {message.Author}{(message.AuthorIsBot ? " (bot)" : string.Empty)}: " +
                ChatContentFraming.CapMessage(message.Text, settings.MaxMessageChars));
        }

        string result = ChatContentFraming.FrameUntrusted(output.ToString().TrimEnd());
        _activity.WriteAtFull(new McpActivityEntry(ChatActivityLogger.Source, "read")
        {
            Target = channel.Name,
            ResponseDetail = result,
        });
        return result;
    }

    /// <summary>Snapshot of every configured channel with its receive capability and poll health.</summary>
    public IReadOnlyList<ChatChannelStatus> ChannelsStatus()
    {
        List<ChatChannelStatus> statuses = [];
        foreach (ChatChannelConfig channel in _repository.LoadChannels())
        {
            IChatProvider? provider = _factory.FindProvider(channel.ProviderType);
            _states.TryGetValue(channel.Id, out ChannelState? state);
            statuses.Add(new ChatChannelStatus(
                channel.Name,
                channel.ProviderType,
                channel.Target,
                channel.Enabled,
                provider?.CanReceive(channel) ?? false,
                state?.LastError,
                state?.LastPollUtc));
        }

        return statuses;
    }

    /// <summary>
    /// Drops a channel's cursor/buffer/waiters after it was edited or deleted so the next
    /// poll starts fresh against the new configuration. Pending asks are cancelled.
    /// </summary>
    public void ResetChannelState(int channelId)
    {
        if (_states.TryRemove(channelId, out ChannelState? state))
        {
            lock (state.Sync)
            {
                foreach (AskWaiter waiter in state.Waiters)
                    waiter.Completion.TrySetCanceled();
                state.Waiters.Clear();
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(ChatChannelConfig Channel, string SentId)> SendCoreAsync(string channelName, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The message text must not be empty.");

        ChatChannelConfig channel = ResolveChannel(channelName);
        IChatProvider provider = _factory.GetProvider(channel);

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            string sentId = await provider.SendAsync(channel, text, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _activity.WriteAtConnectivity(new McpActivityEntry(ChatActivityLogger.Source, "send")
            {
                Target = channel.Name,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                RequestDetail = _activity.FullEnabled ? text : null,
                ResponseDetail = _activity.FullEnabled && sentId.Length > 0 ? $"Message id: {sentId}" : null,
            });

            return (channel, sentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _activity.Write(new McpActivityEntry(ChatActivityLogger.Source, "send")
            {
                Target = channel.Name,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                IsError = true,
                ErrorMessage = ex.Message,
            });
            throw;
        }
    }

    private ChatChannelConfig ResolveChannel(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
            throw new InvalidOperationException("Provide a channel name. Use chat_channels to list configured channels.");

        ChatChannelConfig channel = _repository.FindChannelByName(channelName.Trim())
            ?? throw new InvalidOperationException(
                $"No chat channel named '{channelName.Trim()}' is configured. Use chat_channels to list channels.");

        return channel.Enabled
            ? channel
            : throw new InvalidOperationException($"Chat channel '{channel.Name}' is disabled in the Chat Interface settings.");
    }

    // ── Per-channel state ───────────────────────────────────────────────────

    private sealed class ChannelState
    {
        /// <summary>Guards the buffer, its id set, and the waiter list.</summary>
        public readonly object Sync = new();

        /// <summary>Ensures only one poll per channel runs at a time (loop vs. chat_read).</summary>
        public readonly SemaphoreSlim PollGate = new(1, 1);

        public readonly Queue<ChatIncomingMessage> Buffer = new();
        public readonly HashSet<string> BufferIds = new(StringComparer.Ordinal);
        public readonly List<AskWaiter> Waiters = [];

        public string? Cursor;
        public DateTime NextPollUtc;
        public DateTime LastPollUtc;
        public string? LastError;
        public int ConsecutiveErrors;
    }

    private sealed class AskWaiter
    {
        public required string QuestionMessageId { get; init; }
        public required DateTime SentUtc { get; init; }
        public required TaskCompletionSource<ChatIncomingMessage> Completion { get; init; }
    }
}