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
internal sealed class ChatLine
{
    public string Kind { get; init; } = "assistant"; // "user" | "assistant" | "tool" | "status"
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Binds the tool window to the AgentRuntime: populates the Agent/Mode/Model pills,
/// streams responses into the transcript, and routes Interactive-mode permission prompts.
/// </summary>
internal sealed class ToolWindowViewModel : INotifyPropertyChanged
{
    private readonly ChatEngine _engine;
    private readonly ExtensionSettingsStore _settings;
    private readonly List<AgentMessage> _history = new();
    private CancellationTokenSource? _cts;

    private string _currentAgent = "Agent";
    private string _currentMode = "Interactive";
    private string _currentModel = string.Empty;

    public ToolWindowViewModel(ChatEngine engine, ExtensionSettingsStore settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Built-in agents.
        Agents.Add(new AgentConfig { Name = "Agent", DisplayName = "Agent", SystemPrompt = "You are a capable coding agent.", IsBuiltin = true });
        Agents.Add(new AgentConfig { Name = "Ask", DisplayName = "Ask", SystemPrompt = "You answer questions concisely. Do not use tools.", IsBuiltin = true, Tools = Array.Empty<string>() });
        Agents.Add(new AgentConfig { Name = "Plan", DisplayName = "Plan", SystemPrompt = "You produce implementation plans only; do not edit files.", IsBuiltin = true });

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
            // Remember the selection in defaults.
            var s = _settings.LoadAsync().GetAwaiter().GetResult();
            s.Defaults?.Model = value;
            _settings.SaveAsync(s).GetAwaiter().GetResult();
        }
    }

    /// <summary>Loads agents, connections, and models from the settings store.</summary>
    public async Task LoadAsync()
    {
        var s = await _settings.LoadAsync();

        // User-defined agents (map the settings-store Agent type to AgentConfig).
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

        // Models aggregated across enabled connections; pinned first.
        var pinned = new List<string>();
        var rest = new List<string>();
        foreach (var conn in s.Connections ?? Array.Empty<Connection>())
        {
            if (!conn.Enabled) continue;
            foreach (var m in conn.Models ?? Array.Empty<ModelEntry>())
            {
                var label = m.Pinned ? $"★ {m.Name}" : m.Name;
                (m.Pinned ? pinned : rest).Add(label);
            }
        }
        Models.Clear();
        foreach (var p in pinned) Models.Add(p);
        foreach (var r in rest) Models.Add(r);
        if (Models.Count > 0 && string.IsNullOrEmpty(CurrentModel))
            CurrentModel = Models[0];
    }

    /// <summary>Sends the prompt and streams the agent's response into the transcript.</summary>
    public async Task SendAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (Models.Count == 0)
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

        var events = new AgentEvents
        {
            TextDelta = delta => AppendDelta(delta),
            ToolCallStart = tc => Lines.Add(new ChatLine { Kind = "tool", Text = $"→ {tc.Name}({tc.Arguments?.ToJsonString()})" }),
            ToolCallComplete = (tc, ok, res) => Lines.Add(new ChatLine { Kind = "tool", Text = ok ? $"✓ {tc.Name}" : $"✗ {tc.Name}: {res}" }),
            // Interactive mode: prompt the user per tool.
            RequestPermission = tc => Task.Run(() =>
            {
                // Default to approve for now; a real UI would show a confirm dialog.
                return true;
            }),
            TurnComplete = r => Lines.Add(new ChatLine { Kind = "status", Text = $"[turn complete: {r.ToolCallsExecuted} tool calls]" }),
        };

        _cts = new CancellationTokenSource();
        var streaming = new ChatLine { Kind = "assistant", Text = string.Empty };
        Lines.Add(streaming);

        try
        {
            var result = await _engine.RunAsync(agent, CurrentModel, mode, _history, prompt, events, _cts.Token);
            streaming.Text = result.FinalText;
            _history.Add(new AgentMessage("assistant", result.FinalText));
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
        var last = Lines.Count > 0 ? Lines[^1] : null;
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
