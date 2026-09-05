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

        var ollama = new OllamaApiClient("http://localhost:8388");
        var mcp = new McpServerManager(_settings);
        var engine = new ChatEngine(ollama, new AgentRuntime(ollama, mcp, _settings), _settings);
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
        var prompt = PromptBox.Text;
        PromptBox.Clear();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        SendButton.IsEnabled = false;
        await _vm?.SendAsync(prompt);
        SendButton.IsEnabled = true;
    }

    private void GearButton_Click(object? sender, RoutedEventArgs e)
    {
        var wnd = new Kaeo.LlmProxy.VSExtension.Settings.SettingsWindow();
        wnd.OpenTab("General");
        wnd.Owner = Application.Current?.MainWindow;
        wnd.ShowDialog();
        _ = _vm?.LoadAsync();
    }
}
