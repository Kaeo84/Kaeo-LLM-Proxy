using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// A single log entry for display in the System Logs tab.
/// Shared between the in-memory sink, the DB-backed sink, and the GUI.
/// </summary>
internal sealed record SystemLogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    string? Exception,
    string? SourceContext);

/// <summary>
/// Bounded in-memory Serilog sink that retains the most recent log events for real-time
/// display in the System Logs tab. Entries older than the capacity are dropped.
/// </summary>
internal sealed class SystemLogSink : ILogEventSink, IDisposable
{
    public const int Capacity = 500;

    private readonly object _lock = new();
    private readonly List<SystemLogEntry> _entries = new();

    public void Emit(LogEvent logEvent)
    {
        var entry = new SystemLogEntry(
            logEvent.Timestamp.LocalDateTime,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString(),
            logEvent.Properties.TryGetValue("$sourceContext", out var sc)
                ? sc.ToString().Trim('"')
                : null);

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
                _entries.RemoveRange(0, _entries.Count - Capacity);
        }
    }

    /// <summary>Returns a snapshot of all currently retained entries (oldest first).</summary>
    public List<SystemLogEntry> GetSnapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    /// <summary>Removes all retained entries.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
