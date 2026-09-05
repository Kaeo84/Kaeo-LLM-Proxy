using System.Windows;
using System.Windows.Controls;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

public partial class ToolWindowControl : UserControl
{
    private readonly ExtensionSettingsStore _settings = new();
    private ToolWindowViewModel? _vm;

    public ToolWindowControl()
    {
        InitializeComponent();

        // No hardcoded proxy URL: connections come from settings, and models are pulled
        // live from each connection's Ollama /api/tags endpoint (see ToolWindowViewModel).
        var mcp = new McpServerManager(_settings);
        var engine = new ChatEngine(new AgentRuntime(mcp));
        _vm = new ToolWindowViewModel(engine, _settings);

        // Bind the transcript and pills.
        MessageList.ItemsSource = _vm.Lines;
        AgentCombo.ItemsSource = _vm.Agents;
        AgentCombo.DisplayMemberPath = "DisplayName";
        AgentCombo.SelectedValuePath = "Name";
        AgentCombo.SelectedValue = _vm.CurrentAgent;
        AgentCombo.SelectionChanged += (_, _) => _vm.CurrentAgent = AgentCombo.SelectedValue as string ?? _vm.CurrentAgent;

        ModeCombo.ItemsSource = _vm.Modes;
        ModeCombo.SelectedItem = _vm.CurrentMode;
        ModeCombo.SelectionChanged += (_, _) => _vm.CurrentMode = ModeCombo.SelectedItem as string ?? _vm.CurrentMode;

        ModelCombo.ItemsSource = _vm.Models;
        ModelCombo.SelectionChanged += (_, _) => _vm.CurrentModel = ModelCombo.SelectedItem as string ?? _vm.CurrentModel;

        // After each live model pull, sync the combo selection to the chosen default model.
        _vm.ModelsLoaded += SyncModelSelection;

        // Enter to send (Shift+Enter for newline).
        PromptBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SendButton_Click(this, new RoutedEventArgs());
            }
        };

        GearButton.Click += GearButton_Click;
    }

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var prompt = PromptBox.Text;
            PromptBox.Clear();
            if (string.IsNullOrWhiteSpace(prompt) || _vm is null) return;
            SendButton.IsEnabled = false;
            await _vm.SendAsync(prompt);
            SendButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SendButton.IsEnabled = true;
            MessageBox.Show($"Error sending message: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GearButton_Click(object? sender, RoutedEventArgs e)
    {
        var wnd = new Kaeo.LlmProxy.VSExtension.Settings.SettingsWindow(_settings);
        // After a connection is added/removed or models refreshed, re-pull the live list.
        // LoadAsync fires ModelsLoaded, which syncs the combo selection.
        if (_vm is not null)
        {
            var vm = _vm;
            wnd.ModelsChanged += () => _ = vm.LoadAsync();
        }
        wnd.OpenTab("Models");
        wnd.Owner = Application.Current?.MainWindow;
        wnd.ShowDialog();
    }

    /// <summary>Selects the current model in the combo after a live model pull.</summary>
    private void SyncModelSelection()
    {
        if (_vm is null) return;
        var label = _vm.CurrentModel;
        if (!string.IsNullOrEmpty(label) && _vm.Models.Contains(label))
            ModelCombo.SelectedItem = label;
    }
}
