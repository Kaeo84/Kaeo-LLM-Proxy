using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kaeo.LlmProxy.VSExtension.Core;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

/// <summary>
/// A single line in the chat transcript: a user/assistant message or a tool-activity block.
/// </summary>
internal sealed class ChatLine : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private string _reasoning = string.Empty;
    private bool _isReasoningVisible;
    private bool _isAnswered;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _processing = new();

    public ChatLine()
    {
        // Any processing-entry change re-renders the section header/visibility and
        // scrolls the transcript (the control hooks PropertyChanged for scrolling).
        _processing.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Processing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProcessing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessingHeader)));
        };
    }

    public string Kind { get; init; } = "assistant"; // "user" | "assistant" | "tool" | "status" | "redirect" | "permission"

    /// <summary>
    /// Approval handshake for pending permission cards; null for every other line kind.
    /// The runtime loop awaits it, the card's buttons resolve it - no modal dialog.
    /// </summary>
    public TaskCompletionSource<bool>? Approval { get; set; }

    /// <summary>Raises change notifications so streamed deltas and the final text re-render in place.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    /// <summary>Model reasoning (thinking) streamed for this line, shown inside the Processing section.</summary>
    public string Reasoning
    {
        get => _reasoning;
        set
        {
            if (_reasoning == value) return;
            _reasoning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reasoning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProcessing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessingHeader)));
        }
    }

    /// <summary>Whether the reasoning text is shown (false when empty or the display pref is Hidden).</summary>
    public bool IsReasoningVisible
    {
        get => _isReasoningVisible;
        set
        {
            if (_isReasoningVisible == value) return;
            _isReasoningVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReasoningVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProcessing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessingHeader)));
        }
    }

    /// <summary>
    /// Tool-activity timeline for this turn ("→ name(args)", "✓ name", "✗ name: error"),
    /// rendered inside the foldable Processing section under the assistant message.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Processing => _processing;

    /// <summary>True when the Processing section has anything to show (tool activity or visible reasoning).</summary>
    public bool HasProcessing => _processing.Count > 0 || (_isReasoningVisible && _reasoning.Length > 0);

    /// <summary>Header text for the Processing expander, summarizing its contents.</summary>
    public string ProcessingHeader
    {
        get
        {
            var tools = _processing.Count;
            var thinking = _isReasoningVisible && _reasoning.Length > 0;
            var toolLabel = tools == 1 ? "1 tool call" : $"{tools} tool calls";
            if (thinking && tools > 0) return $"Processing — thinking + {toolLabel}";
            if (thinking) return "Processing — thinking";
            return $"Processing — {toolLabel}";
        }
    }

    /// <summary>True once the user answered a permission card; hides its buttons.</summary>
    public bool IsAnswered
    {
        get => _isAnswered;
        set
        {
            if (_isAnswered == value) return;
            _isAnswered = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnswered)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsApprovalButtons)));
        }
    }

    /// <summary>Whether this line shows the Allow/Always/Deny buttons (unanswered permission card).</summary>
    public bool ShowsApprovalButtons => Kind == "permission" && !_isAnswered;

    /// <summary>Horizontal alignment for this message row ("Left" or "Right").</summary>
    public string HorizontalAlignment { get; set; } = "Left";

    /// <summary>Optional background color hex for this message; null = transparent.</summary>
    public string? BackgroundHex { get; set; }

    /// <summary>Optional text color hex for this message; null = theme default.</summary>
    public string? ForegroundHex { get; set; }

    /// <summary>Computed foreground brush from ForegroundHex; null means "use theme".</summary>
    public System.Windows.Media.SolidColorBrush? ForegroundBrush
    {
        get
        {
            if (string.IsNullOrEmpty(ForegroundHex))
                return null;
            try
            {
                return new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ForegroundHex!));
            }
            catch { return null; }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Binds the tool window to the AgentRuntime: populates the Agent/Mode/Model pills,
/// streams responses into the transcript, and routes Interactive-mode permission prompts.
/// </summary>
internal sealed class ToolWindowViewModel : INotifyPropertyChanged
{
    private readonly ChatEngine _engine;
    private readonly ExtensionSettingsStore _settings;
    private readonly McpServerManager _mcp;
    private readonly List<ChatMessage> _history = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private readonly Queue<string> _redirects = new();

        /// <summary>Current context window size from the selected model (0 = unknown).</summary>
        private long _contextTokensMax;

        /// <summary>Estimated token count of the current conversation history.</summary>
        private int _contextTokensUsed;

        /// <summary>Estimated tokens used by system prompt/instructions only.</summary>
        private int _contextTokensSystem;

        /// <summary>True while a turn is streaming; drives the Send/Stop button toggle.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// A model selectable in the pill bar: the model name, the owning connection (baseUrl + key),
    /// whether the Ollama "tools" capability is present (tool-calling models are auto-enabled),
    /// the connection's upstream kind, whether it is the single pinned default model, and the
    /// per-model reasoning-source override from settings ("Auto" when unset).
    /// </summary>
    public sealed record ModelSelection(string Name, string ConnectionName, string BaseUrl, string? ApiKey, bool SupportsTools, UpstreamKind Kind, bool IsDefault, string? ReasoningSource, long ContextSize);

    private readonly List<ModelSelection> _modelSelections = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>
    /// One chat client per (connection, model, key, reasoning source). Reused across turns
    /// instead of newsing an HttpClient per turn - per-turn churn is a socket-exhaustion
    /// (TIME_WAIT) risk on net48.
    /// </summary>
    private readonly Dictionary<string, OllamaChatClient> _clients = new(StringComparer.Ordinal);

    private string _currentAgent = "Agent";
    private string _currentMode = "Interactive";
    private string _currentModel = string.Empty;

    public ToolWindowViewModel(ChatEngine engine, ExtensionSettingsStore settings, McpServerManager mcp)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));

        // Built-in agents (single source of truth: BuiltinAgents; settings may override them).
        foreach (var b in BuiltinAgents.All) Agents.Add(b);

        Modes.Add("Interactive");
        Modes.Add("Bypass");
        Modes.Add("AutoPilot");

        CurrentAgent = "Agent";
        CurrentMode = "Interactive";

        _ = LoadAsync();
    }

    /// <summary>
    /// Returns all MCP servers that are enabled but currently unhealthy.
    /// </summary>
    public IReadOnlyList<McpServer> GetUnhealthyMcpServers()
    {
        return _mcp.GetUnhealthyServers();
    }

    public ObservableCollection<ChatLine> Lines { get; } = new();
    public ObservableCollection<AgentConfig> Agents { get; } = new();
    public ObservableCollection<string> Modes { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

    private bool _reasoningHidden;
    private bool _reasoningInline;
    private string? _reasoningForegroundHex;
    private string? _reasoningBackgroundHex;

    private string _userMessageAlignment = "Left";
    private string? _userMessageBackgroundHex;
    private string? _userMessageForegroundHex;
    private string _agentMessageAlignment = "Left";
    private string? _agentMessageBackgroundHex;
    private string? _agentMessageForegroundHex;

    /// <summary>True when reasoning content is suppressed entirely (Defaults.ReasoningDisplay = "Hidden").</summary>
    public bool ReasoningHidden
    {
        get => _reasoningHidden;
        private set { _reasoningHidden = value; OnPropertyChanged(); }
    }

    /// <summary>True when reasoning renders always-expanded ("Inline"); otherwise inside a collapsed expander.</summary>
    public bool ReasoningInline
    {
        get => _reasoningInline;
        private set { _reasoningInline = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB override for reasoning text; null falls back to the VS theme.</summary>
    public string? ReasoningForegroundHex
    {
        get => _reasoningForegroundHex;
        private set { _reasoningForegroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB override for the reasoning background; null stays transparent.</summary>
    public string? ReasoningBackgroundHex
    {
        get => _reasoningBackgroundHex;
        private set { _reasoningBackgroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>User message alignment: "Left" (default) or "Right".</summary>
    public string UserMessageAlignment
    {
        get => _userMessageAlignment;
        private set { _userMessageAlignment = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB background for user messages; null = transparent.</summary>
    public string? UserMessageBackgroundHex
    {
        get => _userMessageBackgroundHex;
        private set { _userMessageBackgroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB text color for user messages; null = theme.</summary>
    public string? UserMessageForegroundHex
    {
        get => _userMessageForegroundHex;
        private set { _userMessageForegroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>Agent message alignment: "Left" (default) or "Right".</summary>
    public string AgentMessageAlignment
    {
        get => _agentMessageAlignment;
        private set { _agentMessageAlignment = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB background for agent messages; null = transparent.</summary>
    public string? AgentMessageBackgroundHex
    {
        get => _agentMessageBackgroundHex;
        private set { _agentMessageBackgroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>Optional #RRGGBB text color for agent messages; null = theme.</summary>
    public string? AgentMessageForegroundHex
    {
        get => _agentMessageForegroundHex;
        private set { _agentMessageForegroundHex = value; OnPropertyChanged(); }
    }

    /// <summary>Estimated tokens used by current conversation history.</summary>
    public int ContextTokensUsed
    {
        get => _contextTokensUsed;
        private set { _contextTokensUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContextPercent)); OnPropertyChanged(nameof(ContextTooltip)); }
    }

    /// <summary>Model's maximum context window size in tokens.</summary>
    public long ContextTokensMax
    {
        get => _contextTokensMax;
        private set { _contextTokensMax = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContextPercent)); OnPropertyChanged(nameof(ContextTooltip)); OnPropertyChanged(nameof(IsContextKnown)); }
    }

    /// <summary>Percentage of context window used (0-100).</summary>
    public double ContextPercent =>
        _contextTokensMax > 0 ? Math.Min(100.0, (_contextTokensUsed * 100.0) / _contextTokensMax) : 0;

    /// <summary>Tooltip for the context bar showing token breakdown.</summary>
    public string ContextTooltip
    {
        get
        {
            if (_contextTokensMax > 0)
            {
                var sysPct = _contextTokensMax > 0 ? (_contextTokensSystem * 100.0) / _contextTokensMax : 0;
                var convPct = _contextTokensMax > 0 ? ((_contextTokensUsed - _contextTokensSystem) * 100.0) / _contextTokensMax : 0;
                return $"System: {_contextTokensSystem:N0} tokens ({sysPct:F0}%)\n"
                     + $"Conversation: {_contextTokensUsed - _contextTokensSystem:N0} tokens ({convPct:F0}%)\n"
                     + $"Total: {_contextTokensUsed:N0} / {_contextTokensMax:N0} tokens ({ContextPercent:F0}%)";
            }
            // Unknown model — still show token estimate
            return $"Estimated tokens used: {_contextTokensUsed:N0}\n"
                 + "(set model context size in Settings → Models for percentages)";
        }
    }

    /// <summary>True when the model's context window size is known (set in Settings → Models).</summary>
    public bool IsContextKnown => _contextTokensMax > 0;

    /// <summary>Raised after <see cref="LoadAsync"/> finishes pulling the live model list.</summary>
    public event Action? ModelsLoaded;

    public string CurrentAgent
    {
        get => _currentAgent;
        set { _currentAgent = value; OnPropertyChanged(); }
    }

    public string CurrentMode
    {
        get => _currentMode;
        set { _currentMode = value; OnPropertyChanged(); }
    }

    public string CurrentModel
    {
        get => _currentModel;
        set
        {
            _currentModel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Persists the current model selection into the defaults section of settings.</summary>
    public async Task PersistCurrentModelAsync()
    {
        if (string.IsNullOrEmpty(CurrentModel)) return;
        var s = await _settings.LoadAsync();
        s.Defaults ??= new Defaults();
        s.Defaults.Model = CurrentModel;
        await _settings.SaveAsync(s);
    }

    /// <summary>
    /// Loads agents from settings and pulls the live model list from every enabled connection's
    /// Ollama /api/tags endpoint. Models are displayed grouped by connection; those advertising
    /// the Ollama "tools" capability are auto-enabled for tool calling.
    /// Reloads are serialized: overlapping calls would otherwise interleave clear/add around
    /// the network awaits and duplicate every entry in the model dropdown.
    /// </summary>
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadCoreAsync()
    {
        var s = await _settings.LoadAsync();

        // Reasoning-display preferences (Defaults.ReasoningDisplay + optional colors) snapshot
        // here so transcript changes take effect from the next streamed line onwards.
        var reasoningDefaults = s.Defaults ?? new Defaults();
        string display = ReasoningDisplayKinds.Parse(reasoningDefaults.ReasoningDisplay);
        ReasoningHidden = display == ReasoningDisplayKinds.Hidden;
        ReasoningInline = display == ReasoningDisplayKinds.Inline;
        ReasoningForegroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.ReasoningForeground) ? null : reasoningDefaults.ReasoningForeground;
        ReasoningBackgroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.ReasoningBackground) ? null : reasoningDefaults.ReasoningBackground;
        UserMessageAlignment = string.Equals(reasoningDefaults.UserMessageAlignment, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
        UserMessageBackgroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.UserMessageBackground) ? null : reasoningDefaults.UserMessageBackground;
        UserMessageForegroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.UserMessageForeground) ? null : reasoningDefaults.UserMessageForeground;
        AgentMessageAlignment = string.Equals(reasoningDefaults.AgentMessageAlignment, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
        AgentMessageBackgroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.AgentMessageBackground) ? null : reasoningDefaults.AgentMessageBackground;
        AgentMessageForegroundHex = string.IsNullOrWhiteSpace(reasoningDefaults.AgentMessageForeground) ? null : reasoningDefaults.AgentMessageForeground;

        // Connect to enabled MCP servers and pull their tool definitions (per-server
        // failures are swallowed inside; a dead server just contributes no tools).
        await _mcp.InitializeAsync();

        // Agents: built-ins first (with any saved override applied), then user-defined ones.
        var savedAgents = s.Agents ?? Array.Empty<Agent>();
        var overrides = new Dictionary<string, Agent>(StringComparer.Ordinal);
        foreach (var a in savedAgents)
            if (!string.IsNullOrWhiteSpace(a.Name))
                overrides[a.Name!] = a;

        Agents.Clear();
        foreach (var b in BuiltinAgents.All)
        {
            if (overrides.TryGetValue(b.Name, out var o))
            {
                Agents.Add(new AgentConfig
                {
                    Name = b.Name,
                    DisplayName = b.DisplayName,
                    Description = o.Description ?? b.Description,
                    SystemPrompt = string.IsNullOrWhiteSpace(o.SystemPrompt) ? b.SystemPrompt : o.SystemPrompt!,
                    Tools = b.Tools,
                    DefaultModel = b.DefaultModel,
                    IsBuiltin = true,
                });
            }
            else
            {
                Agents.Add(b);
            }
        }
        foreach (var a in savedAgents)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            if (overrides.ContainsKey(a.Name!)) continue; // already applied as a built-in override
            Agents.Add(new AgentConfig
            {
                Name = a.Name!,
                DisplayName = a.Name!,
                Description = a.Description,
                SystemPrompt = a.SystemPrompt ?? string.Empty,
                Tools = a.Tools,
                DefaultModel = a.DefaultModel,
                IsBuiltin = false
            });
        }

        // Apply the saved default agent (set via Settings → Agents → Make Default).
        var savedDefaultAgent = s.Defaults?.Agent;
        if (!string.IsNullOrEmpty(savedDefaultAgent) && Agents.Any(a => a.Name == savedDefaultAgent))
            CurrentAgent = savedDefaultAgent!;

        // Apply the saved default mode (set via Settings → Modes), when it matches a known
        // mode name; an unknown value falls back to the Interactive default from the ctor.
        var savedDefaultMode = s.Defaults?.Mode;
        if (!string.IsNullOrEmpty(savedDefaultMode) && Modes.Any(m => m == savedDefaultMode))
            CurrentMode = savedDefaultMode!;

        // Pull live models from every enabled connection into local lists first, so the
        // bound collections are only mutated in one synchronous pass at the end.
        var selections = new List<ModelSelection>();
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conn in s.Connections ?? Array.Empty<Connection>())
        {
            if (!conn.Enabled || string.IsNullOrWhiteSpace(conn.BaseUrl)) continue;

            var kind = UpstreamKinds.Parse(conn.Upstream);
            // net48: IsNullOrWhiteSpace has no nullable annotation, so assert non-null.
            var client = UpstreamClientFactory.Create(kind, conn.BaseUrl!, conn.ApiKey);
            var pinned = new HashSet<string>(
                (conn.Models ?? Array.Empty<ModelEntry>()).Where(me => me.Pinned && me.Name is not null).Select(me => me.Name!),
                StringComparer.Ordinal);
            IReadOnlyList<ModelInfo> fetched;
            try
            {
                fetched = await client.GetModelsAsync();
            }
            catch
            {
                // Connection unreachable — skip it (status surfaced elsewhere).
                continue;
            }

            foreach (var m in fetched)
            {
                if (conn.Name is null || conn.BaseUrl is null) continue;

                // Honor the per-model enable flag: a model the user switched off in
                // Settings → Models is excluded even though the connection still serves it.
                var entry = (conn.Models ?? Array.Empty<ModelEntry>())
                    .FirstOrDefault(me => string.Equals(me.Name, m.Name, StringComparison.Ordinal));
                if (entry is { Enabled: false })
                    continue;

                // Label disambiguates same-named models across connections.
                var label = $"{conn.Name} / {m.Name}";
                if (!seen.Add(label)) continue;
                selections.Add(new ModelSelection(m.Name, conn.Name, conn.BaseUrl, conn.ApiKey, m.SupportsTools, kind, pinned.Contains(m.Name), entry?.ReasoningSource, entry?.ContextSize ?? 0));
                labels.Add(label);
            }
        }

        _modelSelections.Clear();
        _modelSelections.AddRange(selections);
        Models.Clear();
        foreach (var label in labels) Models.Add(label);

        // When no connection yielded a model, surface a single placeholder so the user
        // knows to open settings. It never resolves to a real model, so SendAsync short-circuits.
        if (_modelSelections.Count == 0)
        {
            Models.Add(NoModelsPlaceholder);
            CurrentModel = NoModelsPlaceholder;
            ModelsLoaded?.Invoke();
            return;
        }

        // Prefer the pinned default model, then the last-used model, then the first
        // tool-capable model, else the first model.
        if (string.IsNullOrEmpty(CurrentModel) || !Models.Contains(CurrentModel))
        {
            ModelSelection? pick = _modelSelections.FirstOrDefault(sel => sel.IsDefault);
            if (pick is null && !string.IsNullOrEmpty(s.Defaults?.Model))
            {
                var savedLabel = s.Defaults!.Model;
                pick = _modelSelections.FirstOrDefault(sel => $"{sel.ConnectionName} / {sel.Name}" == savedLabel);
            }
            pick ??= _modelSelections.FirstOrDefault(sel => sel.SupportsTools);
            pick ??= _modelSelections.FirstOrDefault();
            if (pick is not null)
            {
                CurrentModel = $"{pick.ConnectionName} / {pick.Name}";
            }
        }

        ModelsLoaded?.Invoke();
                UpdateContextBar();
            }

            /// <summary>
    /// Resolves the current model label back to its connection and a Microsoft.Extensions.AI
    /// chat client for it. Only the Ollama protocol (the proxy) has a chat client today; the
    /// stub upstream kinds surface a clear status line instead of failing mid-stream.
    /// </summary>
    private (IChatClient? Client, string ModelName, string? Error)? ResolveCurrentModel()
    {
        if (string.IsNullOrEmpty(CurrentModel)) return null;
        var sel = _modelSelections.FirstOrDefault(m => $"{m.ConnectionName} / {m.Name}" == CurrentModel);
        if (sel is null) return null;
        if (sel.Kind != UpstreamKind.Ollama)
            return (null, sel.Name, $"The {UpstreamKinds.Display(sel.Kind)} chat client is not implemented yet. Use an Ollama connection (the proxy) for agent turns.");
        // Reuse one client per (connection, model, key, reasoning source) rather than
        // newsing an HttpClient per turn.
        var cacheKey = $"{sel.BaseUrl}|{sel.Name}|{sel.ApiKey}|{sel.ReasoningSource}";
        if (!_clients.TryGetValue(cacheKey, out var client))
        {
            client = new OllamaChatClient(sel.BaseUrl, sel.Name, sel.ApiKey, reasoningSource: ReasoningSources.Parse(sel.ReasoningSource));
            _clients[cacheKey] = client;
        }
        return (client, sel.Name, null);
    }

    /// <summary>Shown in the model dropdown when no connection has returned a model.</summary>
    public const string NoModelsPlaceholder = "Configure Models… (⚙ → Settings)";

    /// <summary>
    /// Sends the prompt and streams the agent's response into the transcript. Submitting
    /// while a turn is running queues the text as a redirect: the live turn injects it at
    /// its next iteration boundary, or a follow-up turn picks it up when the turn ends,
    /// so mid-run input is never lost.
    /// </summary>
    public async Task SendAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        if (IsBusy)
        {
            lock (_redirects) { _redirects.Enqueue(prompt); }
            Lines.Add(new ChatLine { Kind = "redirect", Text = prompt });
            return;
        }

        // Echo the prompt into the transcript so the user's side of the conversation is visible.
        Lines.Add(CreateLine("user", prompt));

        if (await RunTurnAsync(prompt))
        {
            // Redirects typed after the runtime's last drain become follow-up turns so
            // nothing queued during the run is dropped.
            while (true)
            {
                string? redirect;
                lock (_redirects) { redirect = _redirects.Count > 0 ? _redirects.Dequeue() : null; }
                if (redirect is null || !await RunTurnAsync(redirect))
                    break;
            }
        }
    }

    /// <summary>
    /// Runs one agent turn for an already-echoed prompt. Returns true when the turn
    /// completed normally; cancellation or error clears queued redirects - stopping a run
    /// means stopping its follow-ups too.
    /// </summary>
    private async Task<bool> RunTurnAsync(string prompt)
    {
        if (Models.Count == 0 || CurrentModel == NoModelsPlaceholder)
        {
            Lines.Add(new ChatLine { Kind = "status", Text = "No models available. Add a connection in settings (⚙ → Models)." });
            return false;
        }

        var agent = Agents.FirstOrDefault(a => a.Name == CurrentAgent) ?? Agents[0];
        var mode = CurrentMode switch
        {
            "Bypass" => AgentMode.Bypass,
            "AutoPilot" => AgentMode.AutoPilot,
            _ => AgentMode.Interactive
        };

        // Resolve the Ollama connection + model name for the current selection.
        var resolved = ResolveCurrentModel();
        if (resolved is null)
        {
            Lines.Add(new ChatLine { Kind = "status", Text = "No connection/model selected. Add a connection in settings (⚙ → Models)." });
            return false;
        }
        var (client, modelName, clientError) = resolved.Value;
        if (client is null)
        {
            Lines.Add(new ChatLine { Kind = "status", Text = clientError ?? "No usable chat client for the selected model." });
            return false;
        }

        // Create the single assistant line up front and route every text/reasoning delta of
        // this turn into it explicitly. The previous "append to the last line" behavior broke
        // once a tool line was pushed (deltas then spawned a new assistant line, and the
        // final assignment overwrote the orphaned placeholder), and dropped reasoning that
        // arrived right after a tool line.
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var streaming = CreateLine("assistant");
        Lines.Add(streaming);
        var streamingIndex = Lines.Count - 1; // for inserting permission cards above this line

        // Narration from successive tool-loop iterations would otherwise concatenate into one
        // run-on line ("...coveredNow let me check..."). Insert a blank line before the first
        // text delta that follows a completed tool call so each iteration reads as a paragraph.
        var pendingSeparator = false;
        var seenRealContent = false;

        // Permission cards insert above the streaming assistant line so they appear
        // chronologically (before the response they gate). Track the next insertion slot.
        var permissionInsertAt = streamingIndex;

        // Tool activity and thinking live inside the assistant line's foldable Processing
        // section instead of interleaving as separate transcript rows, so each turn reads
        // as: user message, assistant message, [Processing].
        var events = new AgentEvents
        {
            TextDelta = delta =>
            {
                if (pendingSeparator && streaming.Text.Length > 0)
                    streaming.Text += "\n\n";
                pendingSeparator = false;
                // Strip all leading whitespace until the first real character arrives.
                // Models (especially Qwen) emit one or more blank-line deltas before content.
                if (!seenRealContent)
                {
                    delta = delta.TrimStart();
                    if (delta.Length > 0)
                        seenRealContent = true;
                }
                if (delta.Length > 0)
                    streaming.Text += delta;
            },
            ReasoningDelta = delta =>
            {
                if (ReasoningHidden) return;
                streaming.Reasoning += delta;
                streaming.IsReasoningVisible = true;
            },
            DrainRedirects = TakeRedirects,
            ToolCallStart = tc => streaming.Processing.Add($"→ {tc.Name}({TruncateArgs(tc.Arguments)})"),
            ToolCallComplete = (tc, ok, res) =>
            {
                pendingSeparator = true;
                streaming.Processing.Add(ok ? $"✓ {tc.Name}" : $"✗ {tc.Name}: {TruncateForLine(res)}");
            },
            // Interactive mode: permission cards insert above the assistant line so they
            // appear chronologically — before the response they gate, not after it.
            RequestPermission = async tc =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var card = new ChatLine
                {
                    Kind = "permission",
                    Text = $"Allow the model to run \"{tc.Name}\"? {TruncateArgs(tc.Arguments)}",
                    Approval = tcs,
                };
                Lines.Insert(permissionInsertAt, card);
                permissionInsertAt++;
                streamingIndex++; // assistant line shifted down

                var cts = _cts;
                using var registration = cts is null
                    ? default
                    : cts.Token.Register(() => tcs.TrySetResult(false));

                return await tcs.Task;
            },
            TurnComplete = r => Lines.Add(new ChatLine { Kind = "status", Text = $"[turn complete: {r.ToolCallsExecuted} tool calls]" }),
                        // When a new tool-loop iteration starts (model is called again after tool
                        // execution), reset the whitespace tracker so leading blank deltas from
                        // the new stream batch are also stripped.
                        IterationStarting = () => seenRealContent = false,
                    };

        try
        {
            var result = await _engine.RunAsync(client, agent, modelName, mode, _history, prompt, events, _cts.Token);
            // Fill the line only when nothing streamed in (e.g. a tool-only turn that ended
            // with a terse final answer) - otherwise the streamed text is already here and
            // reassigning would duplicate it.
            if (streaming.Text.Length == 0 && result.FinalText.Length > 0)
                streaming.Text = result.FinalText;
            // The tool-iteration budget ran out: the turn ended with no final answer. Say so
            // explicitly instead of leaving the transcript looking like it stopped mid-thought.
            if (!result.Completed)
                Lines.Add(new ChatLine { Kind = "status", Text = $"[stopped: tool iteration limit reached after {result.ToolCallsExecuted} tool calls — ask the model to continue]" });
            _history.Add(new ChatMessage(ChatRole.Assistant, result.FinalText));
            UpdateContextBar();
            _ = PersistCurrentModelAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            streaming.Text = "[cancelled]";
            DenyPendingPermissions();
            ClearRedirects();
            return false;
        }
        catch (Exception ex)
        {
            streaming.Text = $"[error] {ex.Message}";
            DebugLog.Error("The agent turn failed.", ex);
            DenyPendingPermissions();
            ClearRedirects();
            return false;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Pops every queued redirect; the runtime calls this at iteration boundaries.</summary>
    private IReadOnlyList<string> TakeRedirects()
    {
        lock (_redirects)
        {
            if (_redirects.Count == 0)
                return Array.Empty<string>();

            var all = _redirects.ToArray();
            _redirects.Clear();
            return all;
        }
    }

    private void ClearRedirects()
    {
        lock (_redirects) { _redirects.Clear(); }
    }

    /// <summary>Compact one-line rendering of tool arguments for the transcript tool rows.</summary>
    private static string TruncateArgs(System.Text.Json.Nodes.JsonNode? arguments)
    {
        if (arguments is null)
            return string.Empty;
        var json = arguments.ToJsonString();
        return json.Length <= 120 ? json : json.Substring(0, 120) + "…";
    }

    /// <summary>Collapses a tool result to a single short line (failures only show a summary).</summary>
    private static string TruncateForLine(string? result)
    {
        if (result is null || result.Length == 0)
            return string.Empty;
        var first = result.Split('\n')[0].Trim();
        return first.Length <= 160 ? first : first.Substring(0, 160) + "…";
    }

    private bool _alwaysAllowSession;

    /// <summary>
    /// Resolves a permission card (called by its buttons, or by cancellation denial below).
    /// "Always allow" flips the session flag and records that visibly in the transcript.
    /// </summary>
    public void AnswerPermission(ChatLine line, bool allow, bool always)
    {
        if (line.Approval is null || line.IsAnswered)
            return;

        line.IsAnswered = true;
        line.Text += allow
            ? (always ? " - always allowed" : " - allowed")
            : " - denied";

        if (allow && always && !_alwaysAllowSession)
        {
            _alwaysAllowSession = true;
            Lines.Add(new ChatLine { Kind = "status", Text = "Tool calls auto-allowed for the rest of this session." });
        }

        line.Approval.TrySetResult(allow);
    }

    /// <summary>
    /// Cancelling or erroring a turn must not leave cards pending: any unanswered
    /// permission card is denied so the awaited handshake completes.
    /// </summary>
    private void DenyPendingPermissions()
    {
        foreach (var line in Lines)
        {
            if (line.Kind == "permission" && !line.IsAnswered)
                AnswerPermission(line, allow: false, always: false);
        }
    }

    public void Cancel() => _cts?.Cancel();

        /// <summary>
        /// Creates a ChatLine and applies the user/agent message style (alignment, background,
        /// foreground) from the current view-model snapshot. Status/permission/redirect lines
        /// keep their default theme rendering.
        /// </summary>
        private ChatLine CreateLine(string kind, string text = "")
        {
            var line = new ChatLine { Kind = kind, Text = text };
            if (kind == "user")
            {
                line.HorizontalAlignment = _userMessageAlignment;
                line.BackgroundHex = _userMessageBackgroundHex;
                line.ForegroundHex = _userMessageForegroundHex;
            }
            else if (kind == "assistant")
            {
                line.HorizontalAlignment = _agentMessageAlignment;
                line.BackgroundHex = _agentMessageBackgroundHex;
                line.ForegroundHex = _agentMessageForegroundHex;
            }
            return line;
        }

        /// <summary>
        /// Updates the context bar from the current conversation history + model size.
        /// Called after each turn completes and after model selection changes.
        /// </summary>
        private void UpdateContextBar()
        {
            var modelSelection = _modelSelections.FirstOrDefault(
                m => $"{m.ConnectionName} / {m.Name}" == CurrentModel);
            ContextTokensMax = modelSelection?.ContextSize ?? 0;

            // System tokens: first message in history is the merged system prompt
            var systemMsg = _history.FirstOrDefault(m => m.Role == ChatRole.System);
            _contextTokensSystem = systemMsg is not null
                ? Math.Max(1, ((systemMsg.Text?.Length ?? 0) + MessageContentChars(systemMsg)) / 4)
                : 0;

            ContextTokensUsed = EstimateTokenCount(_history);
            OnPropertyChanged(nameof(ContextTooltip));
        }

        /// <summary>
        /// Best-effort token count for a list of chat messages.</summary>
        private static int EstimateTokenCount(List<ChatMessage> messages)
        {
            var totalChars = 0;
            foreach (var msg in messages)
            {
                totalChars += msg.Text?.Length ?? 0;
                totalChars += MessageContentChars(msg);
            }
            return Math.Max(1, totalChars / 4);
        }

        /// <summary>Character count of content items in a single message.</summary>
        private static int MessageContentChars(ChatMessage msg)
        {
            var chars = 0;
            foreach (var content in msg.Contents)
            {
                if (content is TextContent tc)
                    chars += tc.Text?.Length ?? 0;
            }
            return chars;
        }

        /// <summary>
        /// Sends a compaction request
        /// replaces history with the compacted summary, and displays the compacted text
        /// folded inside a new assistant line.
        /// </summary>
        public async Task CompactAsync()
        {
            if (_isBusy) return;

            var resolved = ResolveCurrentModel();
            if (resolved is null || resolved.Value.Client is null) return;

            var (client, modelName, _) = resolved.Value;

            var compactionPrompt = "Please summarize the conversation so far concisely. "
                + "Include all key facts, decisions, and actions. "
                + "This summary will replace the full conversation history to save context. "
                + "Provide ONLY the summary text, no commentary.";

            var compactionHistory = new List<ChatMessage>(_history)
            {
                new ChatMessage(ChatRole.User, compactionPrompt)
            };

            IsBusy = true;
            var streaming = CreateLine("assistant");
            Lines.Add(streaming);

            try
            {
                var result = await _engine.RunAsync(
                    client, Agents.FirstOrDefault(a => a.Name == CurrentAgent) ?? Agents[0],
                    modelName, AgentMode.Interactive, compactionHistory,
                    compactionPrompt,
                    new AgentEvents
                    {
                        TextDelta = delta => streaming.Text += delta,
                        TurnComplete = r =>
                        {
                            // Replace history with just the system prompt (from original history)
                            // plus the compaction result.
                            var systemMsg = _history.FirstOrDefault(m => m.Role == ChatRole.System);
                            _history.Clear();
                            if (systemMsg is not null)
                                _history.Add(systemMsg);
                            _history.Add(new ChatMessage(ChatRole.User,
                                "[Conversation compacted — previous summary:]"));
                            _history.Add(new ChatMessage(ChatRole.Assistant, r.FinalText));

                            streaming.Text = r.FinalText;
                            UpdateContextBar();
                        },
                    });

                // Show the compacted text folded under a status line
                Lines.Add(new ChatLine
                {
                    Kind = "status",
                    Text = $"[context compacted: {ContextTokensUsed:N0} / {ContextTokensMax:N0} tokens ({ContextPercent:F0}%)]"
                });
            }
            catch (Exception ex)
            {
                streaming.Text = $"[compaction error] {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
