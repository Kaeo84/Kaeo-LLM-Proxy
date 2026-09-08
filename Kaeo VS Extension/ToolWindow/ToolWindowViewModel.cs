using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

/// <summary>
/// A single line in the chat transcript: a user/assistant message or a tool-activity block.
/// </summary>
internal sealed class ChatLine : INotifyPropertyChanged
{
    private string _text = string.Empty;

    public string Kind { get; init; } = "assistant"; // "user" | "assistant" | "tool" | "status"

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
    private readonly List<AgentMessage> _history = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// A model selectable in the pill bar: the model name, the owning connection (baseUrl + key),
    /// and whether the Ollama "tools" capability is present (tool-calling models are auto-enabled).
    /// </summary>
    public sealed record ModelSelection(string Name, string ConnectionName, string BaseUrl, string? ApiKey, bool SupportsTools);

    private readonly List<ModelSelection> _modelSelections = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private string _currentAgent = "Agent";
    private string _currentMode = "Interactive";
    private string _currentModel = string.Empty;

    public ToolWindowViewModel(ChatEngine engine, ExtensionSettingsStore settings, McpServerManager mcp)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));

        // Built-in agents.
        Agents.Add(new AgentConfig { Name = "Agent", DisplayName = "Agent", SystemPrompt = "You are a capable coding agent. Use available tools to read, write, and execute code. Be concise in explanations but thorough in code changes.", IsBuiltin = true });
        Agents.Add(new AgentConfig { Name = "Ask", DisplayName = "Ask", SystemPrompt = "You answer questions concisely. Do not use tools. Provide direct, focused answers.", IsBuiltin = true, Tools = Array.Empty<string>() });
        Agents.Add(new AgentConfig
        {
            Name = "Plan",
            DisplayName = "Plan",
            SystemPrompt = "You are a senior software engineer producing structured implementation plans. Do NOT edit files or run commands; plan only. Respond in markdown with exactly these sections: ## Understanding (1-3 sentences restating the task), ## Assumptions (bullet list of decisions and scope boundaries), ## Approach (1-3 paragraphs with specific file/symbol references), ## Key Files (bullet list with one-line reasons), ## Risks and Open Questions (bullet list), ## Steps (numbered checklist, one verb + one target per step, with indented sub-bullets for breakdown). Be concrete: name real files, types, and endpoints. If the request is ambiguous, state assumptions explicitly.",
            IsBuiltin = true
        });

        Modes.Add("Interactive");
        Modes.Add("Bypass");
        Modes.Add("AutoPilot");

        CurrentAgent = "Agent";
        CurrentMode = "Interactive";

        _ = LoadAsync();
    }

    public ObservableCollection<ChatLine> Lines { get; } = new();
    public ObservableCollection<AgentConfig> Agents { get; } = new();
    public ObservableCollection<string> Modes { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

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

        // Connect to enabled MCP servers and pull their tool definitions (per-server
        // failures are swallowed inside; a dead server just contributes no tools).
        await _mcp.InitializeAsync();

        // User-defined agents (map the settings-store Agent type to AgentConfig).
        var hadUserAgents = Agents.Any(a => !a.IsBuiltin);
        if (hadUserAgents)
        {
            // Remove existing user-defined agents before re-adding.
            var builtins = Agents.Where(a => a.IsBuiltin).ToList();
            Agents.Clear();
            foreach (var b in builtins) Agents.Add(b);
        }
        foreach (var a in s.Agents ?? Array.Empty<Agent>())
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            Agents.Add(new AgentConfig
            {
                Name = a.Name,
                DisplayName = a.Name,
                Description = a.Description,
                SystemPrompt = a.SystemPrompt ?? string.Empty,
                Tools = a.Tools,
                DefaultModel = a.DefaultModel,
                IsBuiltin = false
            });
        }

        // Pull live models from every enabled connection into local lists first, so the
        // bound collections are only mutated in one synchronous pass at the end.
        var selections = new List<ModelSelection>();
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conn in s.Connections ?? Array.Empty<Connection>())
        {
            if (!conn.Enabled || string.IsNullOrWhiteSpace(conn.BaseUrl)) continue;

            var client = new OllamaApiClient(conn.BaseUrl, conn.ApiKey);
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

                // Label disambiguates same-named models across connections.
                var label = $"{conn.Name} / {m.Name}";
                if (!seen.Add(label)) continue;
                selections.Add(new ModelSelection(m.Name, conn.Name, conn.BaseUrl, conn.ApiKey, m.SupportsTools));
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

        // Default to the first tool-capable model, else the first model.
        if (string.IsNullOrEmpty(CurrentModel) || !Models.Contains(CurrentModel))
        {
            var firstTools = _modelSelections.FirstOrDefault(sel => sel.SupportsTools);
            var pick = firstTools ?? _modelSelections.FirstOrDefault();
            if (pick is not null)
            {
                CurrentModel = $"{pick.ConnectionName} / {pick.Name}";
            }
        }

        ModelsLoaded?.Invoke();
    }

    /// <summary>Resolves the current model label back to its connection + client.</summary>
    private (OllamaApiClient Client, string ModelName)? ResolveCurrentModel()
    {
        if (string.IsNullOrEmpty(CurrentModel)) return null;
        var sel = _modelSelections.FirstOrDefault(m => $"{m.ConnectionName} / {m.Name}" == CurrentModel);
        if (sel is null) return null;
        return (new OllamaApiClient(sel.BaseUrl, sel.ApiKey), sel.Name);
    }

    /// <summary>Shown in the model dropdown when no connection has returned a model.</summary>
    public const string NoModelsPlaceholder = "Configure Models… (⚙ → Settings)";

    /// <summary>Sends the prompt and streams the agent's response into the transcript.</summary>
    public async Task SendAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        // Echo the prompt into the transcript so the user's side of the conversation is visible.
        Lines.Add(new ChatLine { Kind = "user", Text = prompt });

        if (Models.Count == 0 || CurrentModel == NoModelsPlaceholder)
        {
            Lines.Add(new ChatLine { Kind = "status", Text = "No models available. Add a connection in settings (⚙ → Models)." });
            return;
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
            return;
        }
        var (client, modelName) = resolved.Value;

        var events = new AgentEvents
        {
            TextDelta = delta => AppendDelta(delta),
            ToolCallStart = tc => Lines.Add(new ChatLine { Kind = "tool", Text = $"→ {tc.Name}({tc.Arguments?.ToJsonString()})" }),
            ToolCallComplete = (tc, ok, res) => Lines.Add(new ChatLine { Kind = "tool", Text = ok ? $"✓ {tc.Name}" : $"✗ {tc.Name}: {res}" }),
            // Interactive mode: prompt the user per tool (VS-themed, UI-thread marshaled).
            RequestPermission = tc => Community.VisualStudio.Toolkit.VS.MessageBox.ShowConfirmAsync(
                "Allow tool call",
                $"Allow the model to run \"{tc.Name}\"?\n\n{tc.Arguments?.ToJsonString()}"),
            TurnComplete = r => Lines.Add(new ChatLine { Kind = "status", Text = $"[turn complete: {r.ToolCallsExecuted} tool calls]" }),
        };

        _cts = new CancellationTokenSource();
        var streaming = new ChatLine { Kind = "assistant", Text = string.Empty };
        Lines.Add(streaming);

        try
        {
            var result = await _engine.RunAsync(client, agent, modelName, mode, _history, prompt, events, _cts.Token);
            streaming.Text = result.FinalText;
            _history.Add(new AgentMessage("assistant", result.FinalText));
            _ = PersistCurrentModelAsync();
        }
        catch (OperationCanceledException)
        {
            streaming.Text = "[cancelled]";
        }
        catch (Exception ex)
        {
            streaming.Text = $"[error] {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Appends a streamed delta to the last assistant line.</summary>
    private void AppendDelta(string delta)
    {
        var last = Lines.Count > 0 ? Lines[Lines.Count - 1] : null;
        if (last is { Kind: "assistant" })
            last.Text += delta;
        else
            Lines.Add(new ChatLine { Kind = "assistant", Text = delta });
    }

    public void Cancel() => _cts?.Cancel();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
