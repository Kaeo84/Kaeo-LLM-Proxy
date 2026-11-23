using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
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
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Set when the last <see cref="LoadAsync"/> could not read the file and fell back to
    /// defaults. A subsequent <see cref="SaveAsync"/> must not overwrite the on-disk file in
    /// that case: saving a defaults-only object would wipe real settings that merely failed
    /// to parse. Cleared by a successful load. Static and keyed by path because the process
    /// uses many store instances against one file (tool window, settings window, MCP manager,
    /// per-call instruction stores) — an instance-local flag would let one instance's failed
    /// load be ignored by another instance's save.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_loadFailed = new(StringComparer.OrdinalIgnoreCase);

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
                {
                    DecryptApiKeys(loaded);
                    Migrate(loaded);
                    s_loadFailed[_path] = false;
                    return loaded;
                }
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

            // The on-disk file exists but could not be read/parsed. Mark the load as failed
            // so a later save does not overwrite the (unreadable) real settings with defaults.
            s_loadFailed[_path] = true;
            return new ExtensionSettings();
        }
    }

    /// <summary>
    /// One-time upgrades for settings persisted by older builds. The shipped default for
    /// MaxToolIterations was raised from 10 to 50 (10 truncated multi-file agent tasks like
    /// code reviews); settings files that still carry the old default get the new one so
    /// existing installs are not stuck at the old cap. A user who deliberately set a custom
    /// value keeps it.
    /// </summary>
    private static void Migrate(ExtensionSettings settings)
    {
        settings.Defaults ??= new Defaults();
        // L9: gate the upgrade on the persisted schema version so it runs once. Without this,
        // a user who deliberately sets MaxToolIterations back to 10 would be bumped to 50 on
        // every load. Version 1 = the 10 -> 50 default raise.
        if (settings.SchemaVersion < 1)
        {
            if (settings.Defaults.MaxToolIterations == 10)
                settings.Defaults.MaxToolIterations = 50;
            settings.SchemaVersion = 1;
        }
    }

    public async Task SaveAsync(ExtensionSettings settings)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Coordinated load-modify-save: reloads the current on-disk settings, applies
    /// <paramref name="mutate"/>, and saves — all under the same write lock as
    /// <see cref="SaveAsync"/>. Callers that only own a subset of the settings (e.g. MCP
    /// tool caches or a single instruction entry) use this instead of saving a stale
    /// snapshot that would clobber concurrent edits made elsewhere.
    /// </summary>
    public async Task UpdateAsync(Func<ExtensionSettings, Task> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadAsync().ConfigureAwait(false);
            await mutate(current).ConfigureAwait(false);
            await SaveCoreAsync(current).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Encrypts and writes the settings. Must be called while holding <see cref="_writeLock"/>.
    /// Encryption and serialization happen on a private deep copy so the caller's live object
    /// is never mutated (a concurrent save or reader must never observe ciphertext, and a
    /// failure mid-serialization must never leave encrypted keys in the shared instance).
    /// </summary>
    private async Task SaveCoreAsync(ExtensionSettings settings)
    {
        // Deep copy via a JSON round-trip: all persisted shapes are plain POCOs (computed
        // helpers are [JsonIgnore]), so the copy is faithful and shares no references.
        var copy = JsonSerializer.Deserialize<ExtensionSettings>(JsonSerializer.Serialize(settings))!;
        // A file entry's Content is only the transient editor buffer; the body lives on disk.
        // Never persist it (bloat + stale duplicated content in the settings store).
        if (copy.Instructions is not null)
        {
            foreach (var entry in copy.Instructions)
            {
                if (entry is not null && entry.IsFile)
                    entry.Content = null;
            }
        }
        EncryptApiKeys(copy);
        var text = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });

        // If the last load fell back to defaults, the in-memory object is not a faithful
        // representation of the on-disk file. Back it up before overwriting so real
        // settings are recoverable rather than silently wiped.
        if (s_loadFailed.TryGetValue(_path, out var loadFailed) && loadFailed && File.Exists(_path))
        {
            var backup = $"{_path}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Copy(_path, backup, overwrite: true); }
            catch (Exception ex) { DebugLog.Warn($"Could not back up settings before save: {ex.Message}"); }
        }

        await WriteWithRetryAsync(_path, text).ConfigureAwait(false);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the file atomically with a short retry/backoff loop. The content is written to a
    /// sibling <c>.tmp</c> file first, then swapped into place via <see cref="File.Replace"/>
    /// (atomic on NTFS, keeping a <c>.bak</c>) or <see cref="File.Move"/> when no destination
    /// exists yet — a crash mid-write can never leave a truncated settings.jsonc. Sharing
    /// violations (0x80070020) are transient: another VS instance holding the extension, a
    /// still-running debounced save, or an antivirus/indexer scan. After the retries are
    /// exhausted the exception propagates so the caller can surface it.
    /// </summary>
    private static async Task WriteWithRetryAsync(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        const int maxAttempts = 8;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    // net48: StreamWriter has no WriteAsync for strings (only for chars).
#pragma warning disable VSTHRD103
                    writer.Write(text);
#pragma warning restore VSTHRD103
                }

                if (File.Exists(path))
                    File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(50 * attempt).ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // DPAPI helpers — encrypt Connection.ApiKey / McpServer.ApiKey at rest so they
    // are never written as plaintext to settings.jsonc. The in-memory objects always
    // carry decrypted values; encryption only happens on the wire-to-disk path.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Marker prefix prepended to Base64-encoded DPAPI ciphertext so we can distinguish
    /// encrypted values from plaintext (including legacy plaintext keys from older builds).
    /// </summary>
    private const string DpapiMarker = "!dpapi!";

    /// <summary>
    /// DPAPI entropy bytes bound to this application so a ciphertext blob stolen from
    /// one app cannot be decrypted by a different application under the same user account.
    /// </summary>
    private static readonly byte[] s_dpapiEntropy = Encoding.UTF8.GetBytes("Kaeo.LlmProxy.VSExtension.settings");

    /// <summary>
    /// Encrypts all <see cref="Connection.ApiKey"/> and <see cref="McpServer.ApiKey"/>
    /// values in-place using DPAPI (<see cref="ProtectedData.Protect"/>). Already-encrypted
    /// values (carrying the <see cref="DpapiMarker"/> prefix) are left alone.
    /// </summary>
    private static void EncryptApiKeys(ExtensionSettings settings)
    {
#pragma warning disable CS8602 // Array element nullability not tracked through foreach + local
        if (settings.Connections is not null)
        {
            foreach (var conn in settings.Connections)
            {
                var key = conn?.ApiKey;
                if (!string.IsNullOrEmpty(key) && !key.StartsWith(DpapiMarker, StringComparison.Ordinal))
                    conn.ApiKey = ProtectString(key);
            }
        }

        if (settings.McpServers is not null)
        {
            foreach (var mcp in settings.McpServers)
            {
                var key = mcp?.ApiKey;
                if (!string.IsNullOrEmpty(key) && !key.StartsWith(DpapiMarker, StringComparison.Ordinal))
                    mcp.ApiKey = ProtectString(key);
            }
        }
#pragma warning restore CS8602
    }

    /// <summary>
    /// Decrypts all <see cref="Connection.ApiKey"/> and <see cref="McpServer.ApiKey"/>
    /// values in-place that carry the <see cref="DpapiMarker"/> prefix. Non-prefixed
    /// values (legacy plaintext) are left as-is and will be encrypted on the next save.
    /// </summary>
    private static void DecryptApiKeys(ExtensionSettings settings)
    {
#pragma warning disable CS8602 // Array element nullability not tracked through foreach + local
        if (settings.Connections is not null)
        {
            foreach (var conn in settings.Connections)
            {
                var key = conn?.ApiKey;
                if (!string.IsNullOrEmpty(key) && key.StartsWith(DpapiMarker, StringComparison.Ordinal))
                    conn.ApiKey = TryUnprotect(key, conn?.Name);
            }
        }

        if (settings.McpServers is not null)
        {
            foreach (var mcp in settings.McpServers)
            {
                var key = mcp?.ApiKey;
                if (!string.IsNullOrEmpty(key) && key.StartsWith(DpapiMarker, StringComparison.Ordinal))
                    mcp.ApiKey = TryUnprotect(key, mcp?.Name);
            }
        }
#pragma warning restore CS8602
    }

    /// <summary>
    /// Decrypts one DPAPI key, tolerating a corrupted or foreign-machine blob: a single bad
    /// key must not fail the entire settings load (which would replace every connection,
    /// server, and instruction with defaults in memory). On failure the key is blanked and a
    /// warning logged; the rest of the settings load normally.
    /// </summary>
    private static string? TryUnprotect(string markedKey, string? ownerName)
    {
        try
        {
            return UnprotectString(markedKey.Substring(DpapiMarker.Length));
        }
        catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
        {
            DebugLog.Warn($"Could not decrypt API key for '{ownerName ?? "(unnamed)"}'; clearing it. Re-enter the key in Settings. ({ex.Message})");
            return null;
        }
    }

    private static string ProtectString(string plaintext)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] protectedBytes = ProtectedData.Protect(plainBytes, s_dpapiEntropy, DataProtectionScope.CurrentUser);
        return DpapiMarker + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectString(string protectedBase64)
    {
        byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
        byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, s_dpapiEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}

internal sealed class ExtensionSettings
{
    /// <summary>L9: persisted schema version so one-time migrations run only once.</summary>
    public int SchemaVersion { get; set; }
    public Defaults? Defaults { get; set; } = new Defaults();
    public Connection[]? Connections { get; set; } = [];
    public Agent[]? Agents { get; set; } = [];
    public McpServer[]? McpServers { get; set; } = [];
    public InstructionEntry[]? Instructions { get; set; } = [];
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
    public int MaxToolIterations { get; set; } = 50;

    /// <summary>Maximum AutoPilot auto-continuations per turn (prevents infinite "not done" loops).</summary>
    public int AutoPilotBudget { get; set; } = 5;

    /// <summary>Default state of the Processing expander: "Collapsed" (default) or "Expanded".</summary>
    public string? ProcessingDefault { get; set; } = "Collapsed";

    /// <summary>How model reasoning (thinking) shows in the transcript: "Collapsed" (folded
    /// sub-expander under Processing), "Expanded" (always visible), or "Hidden".</summary>
    public string? ReasoningDisplay { get; set; } = "Collapsed";

    /// <summary>Default state of the Tools sub-expander: "Collapsed" (default) or "Expanded".</summary>
    public string? ToolsDefault { get; set; } = "Collapsed";

    /// <summary>Tool names that have been permanently "always allowed" across sessions.</summary>
    public string[]? AlwaysAllowedTools { get; set; } = [];

    /// <summary>Optional #RRGGBB overrides for reasoning text/background. Null = follow the theme.</summary>
    public string? ReasoningForeground { get; set; }

    /// <summary>Optional #RRGGBB background behind reasoning content. Null = follow the theme.</summary>
    public string? ReasoningBackground { get; set; }

    /// <summary>User message alignment: "Left" (default) or "Right".</summary>
    public string? UserMessageAlignment { get; set; } = "Left";

    /// <summary>Optional #RRGGBB background for user messages. Null = follow the theme.</summary>
    public string? UserMessageBackground { get; set; }

    /// <summary>Optional #RRGGBB text color for user messages. Null = follow the theme.</summary>
    public string? UserMessageForeground { get; set; }

    /// <summary>Agent message alignment: "Left" (default) or "Right".</summary>
    public string? AgentMessageAlignment { get; set; } = "Left";

    /// <summary>Optional #RRGGBB background for agent (assistant) messages. Null = follow the theme.</summary>
    public string? AgentMessageBackground { get; set; }

    /// <summary>Optional #RRGGBB text color for agent messages. Null = follow the theme.</summary>
    public string? AgentMessageForeground { get; set; }

    /// <summary>Optional font family for transcript message text. Null = built-in default stack.</summary>
    public string? ChatFontFamily { get; set; }

    /// <summary>Optional font size (px) for transcript message text. Null = 13.</summary>
    public double? ChatFontSize { get; set; }

    // ── Harness behavior toggles (config-first; edit in settings.jsonc) ──

    /// <summary>Create a shadow-git checkpoint at the start of each tool-enabled turn.</summary>
    public bool AutoCheckpoint { get; set; } = true;

    /// <summary>Maximum checkpoints kept per repository before the shadow history is pruned.</summary>
    public int CheckpointRetention { get; set; } = 20;

    /// <summary>Extra gitignore-style patterns excluded from checkpoints (on top of .git/.vs/bin/obj/node_modules/.kaeo).</summary>
    public string[]? CheckpointExcludes { get; set; }

    /// <summary>Automatically compact context when usage crosses AutoCompactThreshold percent.</summary>
    public bool AutoCompact { get; set; }

    /// <summary>Context-usage percent that triggers auto-compaction (when AutoCompact is on).</summary>
    public int AutoCompactThreshold { get; set; } = 85;

    /// <summary>Truncate tool results larger than ToolResultMaxChars before they enter context.</summary>
    public bool CapToolResults { get; set; }

    /// <summary>Maximum characters kept from a single tool result when CapToolResults is on.</summary>
    public int ToolResultMaxChars { get; set; } = 30_000;

    /// <summary>Auto-approve read-only tools in Interactive mode (mutating tools still prompt).</summary>
    public bool AutoApproveReadOnly { get; set; }

    /// <summary>Interrupt a turn when the same tool call with identical arguments fails repeatedly.</summary>
    public bool DoomLoopGuard { get; set; } = true;

    /// <summary>Number of identical consecutive failing calls that triggers the doom-loop guard.</summary>
    public int DoomLoopThreshold { get; set; } = 3;

    /// <summary>Run a syntax check after file edits and feed errors back before the turn ends.</summary>
    public bool PostEditSyntaxCheck { get; set; }

    /// <summary>Write a structured per-turn trajectory log (prompts, tool calls, timings).</summary>
    public bool TrajectoryLog { get; set; }

    /// <summary>Model used for compaction; empty = the current chat model.</summary>
    public string? CompactionModel { get; set; }

    /// <summary>Auto-detect and inject convention files (AGENTS.md, .cursorrules, CLAUDE.md).</summary>
    public bool DetectConventionFiles { get; set; } = true;

    /// <summary>Use hierarchical (map-reduce) summarization when compacting long histories.</summary>
    public bool HierarchicalCompaction { get; set; }

    /// <summary>Allow spawning isolated sub-agents for noisy exploration tasks.</summary>
    public bool SubAgents { get; set; }

    /// <summary>Edit engine: "line" (default), "ast", or "auto" (AST when available, line fallback).</summary>
    public string? EditMode { get; set; } = "line";
}

internal sealed class Connection
{
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Upstream API flavor: "Ollama" (default), "OpenAI", or "Anthropic".</summary>
    public string? Upstream { get; set; } = "Ollama";
    public ModelEntry[]? Models { get; set; } = [];
}

internal sealed class ModelEntry
{
    public string? Name { get; set; }
    public string[]? Capabilities { get; set; }
    public long ContextSize { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Whether this model is available in the tool window dropdown. Defaults to true so existing settings keep working.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Where this model's reasoning arrives on the wire: "Auto" (default), "ThinkingField", "InlineTags", or "Disabled".</summary>
    public string? ReasoningSource { get; set; }
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
    public McpTool[]? Tools { get; set; } = [];
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
