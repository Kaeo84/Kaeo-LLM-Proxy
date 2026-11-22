using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Writes Chat Interface activity entries into the host's MCP request log (Logs tab, MCP
/// sub-tab). Unlike a per-write lookup, the configured <see cref="ChatSettings.McpLogLevel"/>
/// is cached and refreshed at most every ~5 seconds so logging never adds a database hit per
/// entry. Errors and cancellations are always recorded; lifecycle events at
/// <see cref="ChatMcpLogLevel.Connectivity"/> or higher; full traffic only at
/// <see cref="ChatMcpLogLevel.Full"/>. Never pass secrets or unredacted webhook URLs here.
/// </summary>
internal sealed class ChatActivityLogger(IMcpActivityLog sink, Func<ChatMcpLogLevel> levelProvider)
{
    /// <summary>Source label shown in the log's Method column.</summary>
    public const string Source = "Chat";

    private static readonly TimeSpan LevelCacheDuration = TimeSpan.FromSeconds(5);

    private readonly IMcpActivityLog _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly Func<ChatMcpLogLevel> _levelProvider = levelProvider ?? throw new ArgumentNullException(nameof(levelProvider));
    private readonly object _levelLock = new();
    private ChatMcpLogLevel _cachedLevel = ChatMcpLogLevel.Connectivity;
    private DateTime _nextLevelRefreshUtc;

    /// <summary>Current log level, refreshed from the database at most every ~5 seconds.</summary>
    public ChatMcpLogLevel Level
    {
        get
        {
            lock (_levelLock)
            {
                if (DateTime.UtcNow >= _nextLevelRefreshUtc)
                {
                    try
                    {
                        _cachedLevel = _levelProvider();
                    }
                    catch
                    {
                        // The level lookup reads the module database; on any trouble keep the
                        // last known level quietly (sharing violations must never crash logging).
                    }

                    _nextLevelRefreshUtc = DateTime.UtcNow + LevelCacheDuration;
                }

                return _cachedLevel;
            }
        }
    }

    /// <summary>True when full details (tool arguments and results) should be recorded.</summary>
    public bool FullEnabled => Level == ChatMcpLogLevel.Full;

    /// <summary>True when lifecycle events (sends, replies, poll failures) should be recorded.</summary>
    public bool ConnectivityEnabled => Level >= ChatMcpLogLevel.Connectivity;

    /// <summary>Always records the entry (errors and cancellations).</summary>
    public void Write(McpActivityEntry entry) => _sink.Write(entry);

    /// <summary>Records the entry only when connectivity or full logging is enabled.</summary>
    public void WriteAtConnectivity(McpActivityEntry entry)
    {
        if (ConnectivityEnabled)
            _sink.Write(entry);
    }

    /// <summary>Records the entry only when full/verbose logging is enabled.</summary>
    public void WriteAtFull(McpActivityEntry entry)
    {
        if (FullEnabled)
            _sink.Write(entry);
    }
}