using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Renci.SshNet.Common;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// Writes SSH activity entries into the host's MCP request log (Logs tab, MCP sub-tab). The
/// configured <see cref="SshSettings.McpLogLevel"/> is read live on every write so a change on
/// the SSH tab applies immediately. Errors and cancellations are always recorded; regular tool
/// traffic only at <see cref="SshMcpLogLevel.Full"/>.
/// </summary>
internal sealed class SshActivityLogger(IMcpActivityLog sink, Func<SshMcpLogLevel> levelProvider)
{
    /// <summary>Source label shown in the log's Method column.</summary>
    public const string Source = "SSH";

    private readonly IMcpActivityLog _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly Func<SshMcpLogLevel> _levelProvider = levelProvider ?? throw new ArgumentNullException(nameof(levelProvider));
    private readonly object _levelLock = new();
    private SshMcpLogLevel _cachedLevel = SshMcpLogLevel.Connectivity;
    private long _cachedAtTick;
    private const long LevelCacheTtlMs = 5_000;

    /// <summary>True when full details (tool arguments and results) should be recorded.</summary>
    public bool FullEnabled
    {
        get
        {
            // L1: cache the level so each activity write does not reload the whole settings
            // table. Refreshed on save (InvalidateLevelCache) and at least every few seconds so
            // changes made by another instance are still picked up.
            lock (_levelLock)
            {
                long now = Environment.TickCount64;
                if (_cachedAtTick == 0 || now - _cachedAtTick > LevelCacheTtlMs)
                {
                    try
                    {
                        _cachedLevel = _levelProvider();
                    }
                    catch (Exception ex)
                    {
                        // Keep the previous level on a transient database failure; log at Debug.
                        Log.Debug(ex, "SSH log-level lookup failed; keeping the cached level");
                    }

                    _cachedAtTick = now;
                }

                return _cachedLevel == SshMcpLogLevel.Full;
            }
        }
    }

    /// <summary>Forces the next <see cref="FullEnabled"/> read to re-query the level (after a save).</summary>
    public void InvalidateLevelCache()
    {
        lock (_levelLock)
            _cachedAtTick = 0;
    }

    /// <summary>Always records the entry (connection lifecycle events, errors, cancellations).</summary>
    public void Write(McpActivityEntry entry) => _sink.Write(entry);

    /// <summary>Records the entry only when full/verbose logging is enabled.</summary>
    public void WriteAtFull(McpActivityEntry entry)
    {
        if (FullEnabled)
            _sink.Write(entry);
    }
}
