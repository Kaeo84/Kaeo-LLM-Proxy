using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorActivityLogger
{
    private const int MaxBufferedEntries = 500;

    private readonly IMcpActivityLog _activityLog;
    private readonly Func<CodeVectorMcpLogLevel> _getLogLevel;
    private readonly object _bufferLock = new();
    private readonly List<LogEntry> _buffer = new();
    private long _totalLogged;
    private long _errorCount;

    public CodeVectorActivityLogger(IMcpActivityLog activityLog, Func<CodeVectorMcpLogLevel> getLogLevel)
    {
        _activityLog = activityLog;
        _getLogLevel = getLogLevel;
    }

    public long TotalLogged => Interlocked.Read(ref _totalLogged);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public void Log(string operation, string target, string? detail = null)
    {
        var level = _getLogLevel();
        var isError = operation == "error";
        if (isError) Interlocked.Increment(ref _errorCount);
        Interlocked.Increment(ref _totalLogged);

        if (level != CodeVectorMcpLogLevel.None)
        {
            _activityLog.Write(new McpActivityEntry("CodeVector", operation)
            {
                Target = target,
                RequestDetail = detail,
                IsError = isError,
            });
        }

        var entry = new LogEntry(DateTime.Now, operation, target, detail);
        lock (_bufferLock)
        {
            _buffer.Add(entry);
            if (_buffer.Count > MaxBufferedEntries)
                _buffer.RemoveRange(0, _buffer.Count - MaxBufferedEntries);
        }
    }

    public IReadOnlyList<LogEntry> GetRecentEntries()
    {
        lock (_bufferLock)
            return _buffer.ToList();
    }

    public void ClearBuffer()
    {
        lock (_bufferLock) _buffer.Clear();
    }

    internal sealed record LogEntry(DateTime Timestamp, string Operation, string Target, string? Detail);
}
