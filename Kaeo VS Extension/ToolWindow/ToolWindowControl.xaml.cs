using Community.VisualStudio.Toolkit;
using Kaeo.LlmProxy.VSExtension.Core;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        _vm = new ToolWindowViewModel(engine, _settings, mcp);

        // Bind the transcript and pills.
        MessageList.ItemsSource = _vm.Lines;
        _vm.Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (ChatLine line in e.NewItems)
                line.PropertyChanged += (_, _) => RequestScroll();
            RequestScroll();
        };
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

        // Enter to send (Shift+Enter for newline). WPF reports the main Enter key as
        // Key.Return and the numeric-pad Enter as Key.Enter, so both have to be handled -
        // matching only Key.Enter made the shortcut fire for the numpad key alone.
        // PreviewKeyDown is used so the key is consumed before the TextBox can insert a newline.
        PromptBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Return && e.Key != System.Windows.Input.Key.Enter)
                return;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                return;

            e.Handled = true;
            SendButton_Click(this, new RoutedEventArgs());
        };

        GearButton.Click += GearButton_Click;
    }

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        // A disabled Send button means a turn is already streaming. The button swallows its
        // own clicks in that state, but the Enter shortcut calls this handler directly, so
        // guard here to keep two turns from interleaving into the transcript.
        if (!SendButton.IsEnabled)
            return;

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

    /// <summary>Opens the export format menu anchored to the toolbar button.</summary>
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is null) return;
        ExportButton.ContextMenu.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.IsOpen = true;
    }

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e) => ExportConversation(TranscriptFormat.Markdown);

    private void ExportPlainText_Click(object sender, RoutedEventArgs e) => ExportConversation(TranscriptFormat.PlainText);

    private void ExportHtml_Click(object sender, RoutedEventArgs e) => ExportConversation(TranscriptFormat.Html);

    /// <summary>Writes the conversation transcript to a user-chosen file in the requested format.</summary>
    private void ExportConversation(TranscriptFormat format)
    {
        if (_vm is null) return;

        if (!_vm.Lines.Any(TranscriptFormatter.IsConversation))
        {
            VS.MessageBox.Show("Export Conversation", "There are no messages to export yet.");
            return;
        }

        var (extension, filter) = format switch
        {
            TranscriptFormat.Markdown => ("md", "Markdown files (*.md)|*.md"),
            TranscriptFormat.Html => ("html", "HTML files (*.html)|*.html"),
            _ => ("txt", "Text files (*.txt)|*.txt"),
        };

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Conversation",
            FileName = TranscriptFormatter.DefaultFileName(),
            DefaultExt = extension,
            Filter = filter,
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var text = TranscriptFormatter.Format(_vm.Lines, format);
            // UTF-8 without BOM: a BOM trips Markdown renderers and shows up as mojibake in some editors.
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            VS.MessageBox.ShowError("Export Conversation", $"Could not write the file.\n\n{ex.Message}");
        }
    }

    /// <summary>Copy is only offered when at least one transcript row is selected.</summary>
    private void CopySelected_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = MessageList.SelectedItems.Count > 0;

    private void CopySelected_Executed(object sender, ExecutedRoutedEventArgs e)
        => CopyToClipboard(TranscriptFormatter.ToClipboardText(MessageList.SelectedItems.Cast<ChatLine>()));

    private void CopyAll_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(TranscriptFormatter.ToClipboardText(_vm?.Lines ?? Enumerable.Empty<ChatLine>()));

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; surface it instead of failing silently.
            VS.MessageBox.ShowError("Copy", $"Could not copy to the clipboard.\n\n{ex.Message}");
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

    private bool _scrollPending;

    /// <summary>Coalesces scroll requests so per-token streaming updates scroll once per UI burst.</summary>
    private void RequestScroll()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            _scrollPending = false;
            if (MessageList.Items.Count > 0)
                MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
