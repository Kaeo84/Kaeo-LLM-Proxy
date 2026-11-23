using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// One structured trajectory record (a single JSONL line). Two kinds: "model" (one streamed
/// model call) and "tool" (one tool invocation, including permission denials). Optional fields
/// are omitted from the JSON when null so lines stay compact.
/// </summary>
public sealed class TrajectoryRecord
{
    /// <summary>UTC timestamp, round-trip ("O") format.</summary>
    public string Timestamp { get; private set; } = string.Empty;

    /// <summary>"model" or "tool".</summary>
    public string Kind { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;
    public string Mode { get; private set; } = string.Empty;
    public int Iteration { get; private set; }
    public long LatencyMs { get; private set; }

    // Model-call fields.
    public int? ResponseChars { get; private set; }
    public int? ToolCalls { get; private set; }

    /// <summary>Rough token estimate for the response (chars / 4 heuristic).</summary>
    public int? TokenEstimate { get; private set; }

    /// <summary>Set when the model stream failed; the record is still written.</summary>
    public string? Error { get; private set; }

    // Tool-call fields.
    public string? Tool { get; private set; }

    /// <summary>Short SHA-256 of the arguments JSON — enough to spot identical repeated calls.</summary>
    public string? ArgsHash { get; private set; }

    /// <summary>"ok", "failed", or "denied".</summary>
    public string? Status { get; private set; }

    public int? ResultChars { get; private set; }

    public static TrajectoryRecord ModelCall(
        string model, string mode, int iteration, long latencyMs,
        int responseChars, int toolCalls, string? error = null) => new()
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Kind = "model",
            Model = model,
            Mode = mode,
            Iteration = iteration,
            LatencyMs = latencyMs,
            ResponseChars = responseChars,
            ToolCalls = toolCalls,
            TokenEstimate = responseChars / 4,
            Error = error,
        };

    public static TrajectoryRecord ToolCall(
        string model, string mode, int iteration, string tool, string? argumentsJson,
        string status, int resultChars, long latencyMs) => new()
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Kind = "tool",
            Model = model,
            Mode = mode,
            Iteration = iteration,
            LatencyMs = latencyMs,
            Tool = tool,
            ArgsHash = HashArguments(argumentsJson),
            Status = status,
            ResultChars = resultChars,
        };

    /// <summary>First 8 bytes of the SHA-256 of the arguments JSON as 16 hex chars; empty for null/empty.</summary>
    public static string HashArguments(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson)) return string.Empty;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(argumentsJson!));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// Append-only JSONL trajectory log for deterministic replay and observability
/// (Defaults.TrajectoryLog, off by default). Writes to <c>trace.jsonl</c> in the given
/// directory and rotates it into timestamped archives once it exceeds the size cap
/// (10 MB default). Archives are kept — prune them manually if disk space matters.
/// Every I/O failure degrades to a warning callback; logging must never break a turn.
/// </summary>
public sealed class TrajectoryLog
{
    public const long DefaultMaxFileBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly Action<string>? _warn;
    private readonly object _gate = new();

    public TrajectoryLog(string directory, long maxFileBytes = DefaultMaxFileBytes, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory is required.", nameof(directory));
        if (maxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        _directory = directory;
        _maxFileBytes = maxFileBytes;
        _warn = warn;
    }

    public string ActiveFilePath => Path.Combine(_directory, "trace.jsonl");

    /// <summary>Serializes and appends one record, rotating first when the active file is full.</summary>
    public void Append(TrajectoryRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        try
        {
            var line = JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine;
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = ActiveFilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length + Encoding.UTF8.GetByteCount(line) > _maxFileBytes)
                {
                    var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var archive = Path.Combine(_directory, $"trace-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{suffix}.jsonl");
                    File.Move(path, archive);
                }
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Trajectory log write failed (ignored): {ex.Message}");
        }
    }
}
