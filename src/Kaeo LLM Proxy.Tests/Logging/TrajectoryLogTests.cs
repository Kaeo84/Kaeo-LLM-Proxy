using System.Text.Json;
using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the JSONL trajectory log: record shape (camelCase, null fields omitted),
/// append semantics, size-based rotation, argument hashing, and graceful degradation
/// when the file is locked by another process/instance.
/// </summary>
public class TrajectoryLogTests : IDisposable
{
    private readonly string _dir;

    public TrajectoryLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kaeo-traj-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TrajectoryRecord Model(int responseChars = 400, int toolCalls = 2, string? error = null) =>
        TrajectoryRecord.ModelCall("qwen3", "Agent", 3, 1234, responseChars, toolCalls, error);

    [Fact]
    public void Append_ModelRecord_WritesCamelCaseJsonl()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(Model());

        var lines = File.ReadAllLines(log.ActiveFilePath);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("model", root.GetProperty("kind").GetString());
        Assert.Equal("qwen3", root.GetProperty("model").GetString());
        Assert.Equal("Agent", root.GetProperty("mode").GetString());
        Assert.Equal(3, root.GetProperty("iteration").GetInt32());
        Assert.Equal(1234, root.GetProperty("latencyMs").GetInt64());
        Assert.Equal(400, root.GetProperty("responseChars").GetInt32());
        Assert.Equal(2, root.GetProperty("toolCalls").GetInt32());
        Assert.Equal(100, root.GetProperty("tokenEstimate").GetInt32());
        Assert.True(DateTime.TryParse(root.GetProperty("timestamp").GetString(), out _));
        // Null optional fields are omitted.
        Assert.False(root.TryGetProperty("tool", out _));
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("status", out _));
    }

    [Fact]
    public void Append_FailedModelRecord_IncludesError()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(Model(responseChars: 50, toolCalls: 0, error: "stream reset"));

        using var doc = JsonDocument.Parse(File.ReadAllLines(log.ActiveFilePath)[0]);
        Assert.Equal("stream reset", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("toolCalls").GetInt32());
    }

    [Fact]
    public void Append_ToolRecord_IncludesStatusHashAndSize()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(TrajectoryRecord.ToolCall("qwen3", "Interactive", 1, "vs_build",
            "{\"project\":\"App.csproj\"}", "ok", 2500, 800));

        using var doc = JsonDocument.Parse(File.ReadAllLines(log.ActiveFilePath)[0]);
        var root = doc.RootElement;
        Assert.Equal("tool", root.GetProperty("kind").GetString());
        Assert.Equal("vs_build", root.GetProperty("tool").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(2500, root.GetProperty("resultChars").GetInt32());
        Assert.Equal(800, root.GetProperty("latencyMs").GetInt64());
        Assert.Equal(TrajectoryRecord.HashArguments("{\"project\":\"App.csproj\"}"),
            root.GetProperty("argsHash").GetString());
        Assert.False(root.TryGetProperty("responseChars", out _));
    }

    [Fact]
    public void Append_DeniedToolRecord_StatusDenied()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(TrajectoryRecord.ToolCall("m", "Interactive", 0, "vs_run_command", null, "denied", 24, 0));

        using var doc = JsonDocument.Parse(File.ReadAllLines(log.ActiveFilePath)[0]);
        Assert.Equal("denied", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("argsHash").GetString());
    }

    [Fact]
    public void Append_MultipleRecords_OneLinePerRecord()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(Model());
        log.Append(TrajectoryRecord.ToolCall("m", "Agent", 0, "t", "{}", "ok", 1, 1));
        log.Append(Model());

        var lines = File.ReadAllLines(log.ActiveFilePath);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
            using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public void Append_NoUtf8Bom()
    {
        var log = new TrajectoryLog(_dir);
        log.Append(Model());

        var bytes = File.ReadAllBytes(log.ActiveFilePath);
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Append_ExceedingCap_RotatesAndKeepsAllRecords()
    {
        var log = new TrajectoryLog(_dir, maxFileBytes: 300);
        for (var i = 0; i < 10; i++)
            log.Append(Model());

        var active = new FileInfo(log.ActiveFilePath);
        Assert.True(active.Exists);
        Assert.True(active.Length <= 300, $"active file {active.Length} bytes exceeds cap");

        var archives = Directory.GetFiles(_dir, "trace-*.jsonl");
        Assert.NotEmpty(archives);

        var totalLines = File.ReadAllLines(log.ActiveFilePath).Length
            + archives.Sum(a => File.ReadAllLines(a).Length);
        Assert.Equal(10, totalLines);
    }

    [Fact]
    public void Append_LockedFile_DegradesToWarning()
    {
        var log = new TrajectoryLog(_dir, warn: w => _warnings.Add(w));
        log.Append(Model()); // create the file

        // Simulate a second app instance holding the file without sharing.
        using (var holder = new FileStream(log.ActiveFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            log.Append(Model()); // must not throw
        }

        Assert.Single(_warnings);
        Assert.Contains("Trajectory log write failed", _warnings[0]);
        // The original line is intact; the blocked append was dropped, not corrupted.
        Assert.Single(File.ReadAllLines(log.ActiveFilePath));
    }

    private readonly List<string> _warnings = [];

    [Fact]
    public void Append_NullRecord_Throws()
    {
        var log = new TrajectoryLog(_dir);
        Assert.Throws<ArgumentNullException>(() => log.Append(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidDirectory_Throws(string directory)
    {
        Assert.Throws<ArgumentException>(() => new TrajectoryLog(directory));
    }

    [Fact]
    public void Ctor_NonPositiveCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrajectoryLog(_dir, maxFileBytes: 0));
    }

    [Fact]
    public void HashArguments_StableDistinctAndEmptyForNull()
    {
        var a = TrajectoryRecord.HashArguments("{\"x\":1}");
        var b = TrajectoryRecord.HashArguments("{\"x\":1}");
        var c = TrajectoryRecord.HashArguments("{\"x\":2}");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(16, a.Length);
        Assert.All(a, ch => Assert.Contains(ch, "0123456789abcdef"));
        Assert.Equal("", TrajectoryRecord.HashArguments(null));
        Assert.Equal("", TrajectoryRecord.HashArguments(""));
    }

    [Fact]
    public void HashArguments_UnicodeArgs_HashesUtf8()
    {
        var hash = TrajectoryRecord.HashArguments("{\"path\":\"café/日本\"}");
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash, TrajectoryRecord.HashArguments("{\"path\":\"café/日本\"}"));
    }
}
