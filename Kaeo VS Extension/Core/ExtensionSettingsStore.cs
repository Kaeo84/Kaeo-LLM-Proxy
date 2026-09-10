using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal sealed class ExtensionSettingsStore
{
    private readonly string _path;

    /// <summary>
    /// Serializes writes within this process: the tool window, the settings window, and the
    /// MCP manager all save through (possibly several) store instances against one file, and
    /// the debounced auto-save can overlap a previous save.
    /// </summary>
    private static readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

    public ExtensionSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaeoVsExtension", "settings.jsonc");
    }

    public async Task<ExtensionSettings> LoadAsync()
    {
        if (!File.Exists(_path))
            return new ExtensionSettings();

        // Retry transient failures: another process may hold the file open (second devenv
        // instance) or we may have caught it mid-write (truncated). Falling back to defaults
        // too eagerly risks a later save wiping real settings, so try a few times first.
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // File.ReadAllText is net48-safe; run it off the UI thread. Read with
                // FileShare.ReadWrite so a concurrent writer never makes the read throw.
                var text = await Task.Run(() => ReadShared(_path)).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<ExtensionSettings>(text);
                if (loaded is not null)
                    return loaded;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is JsonException))
            {
                await Task.Delay(75 * attempt).ConfigureAwait(false);
                continue;
            }
            catch
            {
                // Fall through to defaults below.
            }

            return new ExtensionSettings();
        }
    }

    public async Task SaveAsync(ExtensionSettings settings)
    {
        var text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteWithRetry(_path, text)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the file with a short retry/backoff loop. Sharing violations (0x80070020) are
    /// transient: another VS instance holding the extension, a still-running debounced save,
    /// or an antivirus/indexer scan. After the retries are exhausted the exception propagates
    /// so the caller can surface it.
    /// </summary>
    private static void WriteWithRetry(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write(text);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt); // 50, 100, 150 ... ms
            }
        }
    }
}

internal sealed class ExtensionSettings
{
    public Defaults? Defaults { get; set; } = new Defaults();
    public Connection[]? Connections { get; set; } = Array.Empty<Connection>();
    public Agent[]? Agents { get; set; } = Array.Empty<Agent>();
    public McpServer[]? McpServers { get; set; } = Array.Empty<McpServer>();
    public InstructionEntry[]? Instructions { get; set; } = Array.Empty<InstructionEntry>();
    public Logging? Logging { get; set; } = new Logging();
}

internal sealed class Defaults
{
    public string? Agent { get; set; }
    public string? Mode { get; set; }
    public string? Model { get; set; }
    public bool AutoAttachContext { get; set; } = true;

    /// <summary>Sampling temperature sent with each chat request (0-2).</summary>
    public double DefaultTemperature { get; set; } = 0.2;

    /// <summary>How often enabled MCP servers are health-probed, in minutes.</summary>
    public int HeartbeatMinutes { get; set; } = 5;

    /// <summary>Maximum tool-call iterations before a turn is forced to conclude.</summary>
    public int MaxToolIterations { get; set; } = 10;
}

internal sealed class Connection
{
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Upstream API flavor: "Ollama" (default), "OpenAI", or "Anthropic".</summary>
    public string? Upstream { get; set; } = "Ollama";
    public ModelEntry[]? Models { get; set; } = Array.Empty<ModelEntry>();
}

internal sealed class ModelEntry
{
    public string? Name { get; set; }
    public string[]? Capabilities { get; set; }
    public long ContextSize { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Whether this model is available in the tool window dropdown. Defaults to true so existing settings keep working.</summary>
    public bool Enabled { get; set; } = true;
}

internal sealed class Agent
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public string[]? Tools { get; set; }
    public string? DefaultModel { get; set; }
}

internal sealed class McpServer
{
    public string? Name { get; set; }
    public string? Transport { get; set; }
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? Command { get; set; }
    public string[]? Args { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Stale { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastError { get; set; }
    public McpTool[]? Tools { get; set; } = Array.Empty<McpTool>();
}

internal sealed class McpTool
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonNode? Schema { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A single instruction source shown in the Settings → Instructions list and bound directly in the
/// editor. It is both the persisted shape and the editable view model: <see cref="NotifyPropertyChanged"/>
/// drives the master/detail editor, while only the data properties serialize (computed helpers are ignored).
/// A <see cref="Kind"/> of <c>text</c> stores its body inline in <see cref="Content"/>; a <c>file</c>
/// stores only <see cref="Path"/> and reads its body from disk (so <see cref="Content"/> is just the
/// transient editor buffer and is never persisted for file entries).
/// </summary>
public sealed class InstructionEntry : INotifyPropertyChanged
{
    private string? _name;
    private string _kind = InstructionKind.Text;
    private string? _path;
    private string? _content;
    private bool _enabled = true;
    private int _order;
    private string _scope = InstructionScopeKind.Solution;
    private bool _isDefault;

    /// <summary>Display title shown in the Instructions list and editor.</summary>
    public string? Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
    }

    /// <summary>Source kind: <see cref="InstructionKind.Text"/> (inline) or <see cref="InstructionKind.File"/>.</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(KindIndex));
            OnPropertyChanged(nameof(KindLabel));
        }
    }

    /// <summary>File path for a <c>file</c> entry (absolute, or relative to the solution root).</summary>
    public string? Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(nameof(Path)); OnPropertyChanged(nameof(DisplayName)); }
    }

    /// <summary>Inline body for a <c>text</c> entry; for a <c>file</c> entry this is the editor buffer only.</summary>
    public string? Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(nameof(Content)); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(nameof(Enabled)); }
    }

    public int Order
    {
        get => _order;
        set { _order = value; OnPropertyChanged(nameof(Order)); }
    }

    public string Scope { get => _scope; set { _scope = value; OnPropertyChanged(nameof(Scope)); OnPropertyChanged(nameof(ScopeIndex)); OnPropertyChanged(nameof(ScopeLabel)); } }

    public bool IsDefault { get => _isDefault; set { _isDefault = value; OnPropertyChanged(nameof(IsDefault)); } }

    [JsonIgnore] public int ScopeIndex { get => string.Equals(_scope, InstructionScopeKind.Global, StringComparison.OrdinalIgnoreCase) ? 0 : string.Equals(_scope, InstructionScopeKind.Project, StringComparison.OrdinalIgnoreCase) ? 2 : 1; set => Scope = value == 0 ? InstructionScopeKind.Global : value == 2 ? InstructionScopeKind.Project : InstructionScopeKind.Solution; }

    [JsonIgnore] public string ScopeLabel => _scope;

    [JsonIgnore]
    public bool IsFile => string.Equals(_kind, InstructionKind.File, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsText => !IsFile;

    /// <summary>Combo index for the Text/File picker (0 = Text, 1 = File).</summary>
    [JsonIgnore]
    public int KindIndex
    {
        get => IsFile ? 1 : 0;
        set => Kind = value == 1 ? InstructionKind.File : InstructionKind.Text;
    }

    [JsonIgnore]
    public string KindLabel => IsFile ? "(file)" : "(text)";

    /// <summary>List label: the name, falling back to the file name, then a generic placeholder.</summary>
    [JsonIgnore]
    public string DisplayName
        => !string.IsNullOrWhiteSpace(_name) ? _name!
         : !string.IsNullOrWhiteSpace(_path) ? System.IO.Path.GetFileName(_path!)
         : "Instruction";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Well-known <see cref="InstructionEntry.Kind"/> values.</summary>
internal static class InstructionKind
{
    public const string Text = "text";
    public const string File = "file";
}

/// <summary>Well-known <see cref="InstructionEntry.Scope"/> values.</summary>
internal static class InstructionScopeKind
{
    public const string Global = "global";
    public const string Solution = "solution";
    public const string Project = "project";
}

internal sealed class Logging
{
    public string? Level { get; set; } = "Information";
    public string? File { get; set; }
}
