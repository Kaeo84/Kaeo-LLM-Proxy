using Community.VisualStudio.Toolkit;
using Kaeo.LlmProxy.VSExtension.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

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

            VS.MessageBox.ShowError(
                "Error",
                $"Error sending message: {ex.Message}"
            );
        }
    }

    private void GearButton_Click(object? sender, RoutedEventArgs e)
    {
        // 1. Force the execution onto Visual Studio's main UI thread immediately
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

        var uiShell = (IVsUIShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell));
        if (uiShell == null) return;

        var wnd = new Kaeo.LlmProxy.VSExtension.Settings.SettingsWindow(_settings);

        wnd.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Parent to Visual Studio's dialog-owner window so modality, centering, and
        // theming behave like a native VS dialog (WindowInteropHelper needs the raw HWND;
        // HwndSource.FromHwnd(...).RootVisual is null for the native VS shell window).
        uiShell.GetDialogOwnerHwnd(out IntPtr hwndOwner);
        new WindowInteropHelper(wnd).Owner = hwndOwner;

        // Refresh the tool window's model list whenever the settings window persists a change.
        Action? onModelsChanged = () => _ = _vm?.LoadAsync();
        wnd.ModelsChanged += onModelsChanged;
        try
        {
            wnd.ShowDialog();
        }
        finally
        {
            wnd.ModelsChanged -= onModelsChanged;
        }
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
