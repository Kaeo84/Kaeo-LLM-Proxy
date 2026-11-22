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
    // L4: keep the manager and the package-disposal registration so they can be released on Unloaded.
    private readonly McpServerManager _mcp;
    private System.Threading.CancellationTokenRegistration _disposalRegistration;
    // L5: the single MCP health warning line, updated/removed as health changes (no text-match dedup).
    private ChatLine? _mcpWarningLine;
    // One reusable handler so a ChatLine removed from the transcript can be unsubscribed;
    // per-line lambdas could never be detached and would pin the control for the line's lifetime.
    private readonly System.ComponentModel.PropertyChangedEventHandler _lineChangedHandler;

    public ToolWindowControl()
    {
        InitializeComponent();

        // No hardcoded proxy URL: connections come from settings, and models are pulled
        // live from each connection's Ollama /api/tags endpoint (see ToolWindowViewModel).
        _mcp = new McpServerManager(_settings);
        var engine = new ChatEngine(new AgentRuntime(_mcp, _settings));
        _vm = new ToolWindowViewModel(engine, _settings, _mcp);
        // The transcript DataTemplate binds {Binding DataContext.Reasoning*} via RelativeSource,
        // and the Send/Stop trigger binds IsBusy - both need the view model as the visual root's
        // DataContext. Item templates still get ChatLine as their own DataContext.
        DataContext = _vm;

        // Bind the transcript and pills.
        _lineChangedHandler = (_, _) => RequestScroll();
        MessageList.ItemsSource = _vm.Lines;
        _vm.Lines.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (ChatLine line in e.OldItems)
                    line.PropertyChanged -= _lineChangedHandler;
            if (e.NewItems is not null)
                foreach (ChatLine line in e.NewItems)
                    line.PropertyChanged += _lineChangedHandler;
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

        // L4: wire MCP health monitoring on Loaded and release it on Unloaded so a hidden/closed
        // tool window does not keep the manager (and this control) alive via the event subscription.
        Loaded += (_, _) =>
        {
            _mcp.ServerHealthChanged -= OnMcpServerHealthChanged;
            _mcp.ServerHealthChanged += OnMcpServerHealthChanged;
            if (VsPackage.Instance is { } pkg)
            {
                _disposalRegistration.Dispose();
                _disposalRegistration = pkg.DisposalToken.Register(_mcp.StopHeartbeat);
            }
        };
        Unloaded += (_, _) =>
        {
            _mcp.ServerHealthChanged -= OnMcpServerHealthChanged;
            _disposalRegistration.Dispose();
            _disposalRegistration = default;
        };
    }

    // The health event arrives on a background thread (the manager uses ConfigureAwait(false));
    // this is a plain WPF control with no package handle, so Dispatcher is the right marshal here
    // (same trade-off as DebugOutputControl.OnLineWritten and RequestScroll below).
#pragma warning disable VSTHRD001, VSTHRD110
    private void OnMcpServerHealthChanged(McpServer server, bool isHealthy)
    {
        // Must marshal to UI thread
        Dispatcher.InvokeAsync(() =>
        {
            var unhealthy = _vm?.GetUnhealthyMcpServers() ?? Array.Empty<McpServer>();

            if (unhealthy.Count > 0)
            {
                // Show alert bar
                McpAlertBar.Visibility = Visibility.Visible;
                McpAlertText.Text = $"{unhealthy.Count} MCP server(s) unavailable";

                var details = string.Join("\n", unhealthy.Select(s => $"• {s.Name}: {s.LastError ?? "Unknown error"}"));
                McpAlertDetails.Text = details;

                // L5: track one warning line by reference and update it in place instead of
                // deduping on its text (which never refreshed the count/names and never removed it).
                if (_vm != null)
                {
                    var text = $" {unhealthy.Count} MCP server(s) unavailable: {string.Join(", ", unhealthy.Select(s => s.Name))}";
                    if (_mcpWarningLine is null)
                    {
                        _mcpWarningLine = new ChatLine { Kind = "warning", Text = text };
                        _vm.Lines.Add(_mcpWarningLine);
                    }
                    else
                    {
                        _mcpWarningLine.Text = text;
                    }
                }
            }
            else
            {
                // Hide alert bar and drop the stale warning line once everything is healthy again.
                McpAlertBar.Visibility = Visibility.Collapsed;
                if (_vm != null && _mcpWarningLine is not null)
                {
                    _vm.Lines.Remove(_mcpWarningLine);
                    _mcpWarningLine = null;
                }
            }
        });
    }

    /// <summary>
    /// One button, two states. A direct click while a turn streams acts as Stop; the Enter
    /// shortcut routes through here with the control as sender so typed text is submitted -
    /// while busy the view model turns it into a redirect instead of a second concurrent turn.
    /// </summary>
    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        if (ReferenceEquals(sender, SendButton) && _vm.IsBusy)
        {
            _vm.Cancel();
            return;
        }

        var prompt = PromptBox.Text;
        PromptBox.Clear();
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        try
        {
            await _vm.SendAsync(prompt);
        }
        catch (Exception ex)
        {
            DebugLog.Error("Sending the message failed.", ex);

            VS.MessageBox.ShowError(
                "Error",
                $"Error sending message: {ex.Message}"
            );
        }
    }

    // Permission card buttons (transcript DataTemplate): resolving the line's Approval
    // releases the awaited runtime loop - the GUI was never blocked in the first place.
    private void AllowPermission_Click(object sender, RoutedEventArgs e) => AnswerPermissionCard(sender, allow: true, always: false);

    private void AlwaysPermission_Click(object sender, RoutedEventArgs e) => AnswerPermissionCard(sender, allow: true, always: true);

    private void DenyPermission_Click(object sender, RoutedEventArgs e) => AnswerPermissionCard(sender, allow: false, always: false);

    private void AnswerPermissionCard(object sender, bool allow, bool always)
    {
        if (sender is FrameworkElement { DataContext: ChatLine line })
            _vm?.AnswerPermission(line, allow, always);
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
        // Debounced: rapid-fire ModelChanged events (e.g., agent/MCP/instruction saves also fire
        // it) are coalesced so the dropdowns only rebuild once instead of flickering on each edit.
        var debounceCts = new CancellationTokenSource();
        Action? onModelsChanged = () =>
        {
            // L6: dispose the superseded CTS instead of leaking one per ModelsChanged event.
            var previous = debounceCts;
            debounceCts = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            _ = DebouncedReloadAsync(debounceCts.Token);
        };
        wnd.ModelsChanged += onModelsChanged;
        try
        {
            wnd.ShowDialog();
        }
        finally
        {
            wnd.ModelsChanged -= onModelsChanged;
            debounceCts.Cancel();
            debounceCts.Dispose();
        }
    }

    /// <summary>Waits 250 ms then reloads, unless a newer reload cancels this one.</summary>
    private async Task DebouncedReloadAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (_vm is not null)
                await _vm.LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a more recent change.
        }
        catch (ObjectDisposedException)
        {
            // The CTS was disposed right after cancellation (L6); treat as superseded.
        }
    }

    /// <summary>Shows (or focuses) the Kaeo Debug tool window.</summary>
    private async void DebugButton_Click(object sender, RoutedEventArgs e)
    {
        // The package owns ShowToolWindowAsync because its DisposalToken is protected.
        var pkg = VsPackage.Instance;
        if (pkg is null)
            return;

        try
        {
            await pkg.ShowDebugWindowAsync();
        }
        catch (Exception ex)
        {
            VS.MessageBox.ShowError("Debug Window", $"Could not open the debug window.\n\n{ex.Message}");
        }
    }

    /// <summary>Opens the export format menu anchored to the toolbar button.</summary>
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is null) return;
        ExportButton.ContextMenu.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.IsOpen = true;
    }

    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e) => await ExportConversationAsync(TranscriptFormat.Markdown);

    private async void ExportPlainText_Click(object sender, RoutedEventArgs e) => await ExportConversationAsync(TranscriptFormat.PlainText);

    private async void ExportHtml_Click(object sender, RoutedEventArgs e) => await ExportConversationAsync(TranscriptFormat.Html);

    /// <summary>Writes the conversation transcript to a user-chosen file in the requested format.</summary>
    private async System.Threading.Tasks.Task ExportConversationAsync(TranscriptFormat format)
    {
        if (_vm is null) return;

        if (!_vm.Lines.Any(TranscriptFormatter.IsConversation))
        {
            await VS.MessageBox.ShowAsync("Export Conversation", "There are no messages to export yet.");
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
            var path = dialog.FileName;
            // UTF-8 without BOM: a BOM trips Markdown renderers and shows up as mojibake in some editors.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            // L11: write off the UI thread (net48 has no File.WriteAllTextAsync).
            await System.Threading.Tasks.Task.Run(() => File.WriteAllText(path, text, encoding));
        }
        catch (IOException ex)
        {
            // L11 / AllowMultipleInstances: a sharing violation must degrade gracefully, not crash.
            DebugLog.Warn($"Export conversation could not write '{dialog.FileName}': {ex.Message}");
            await VS.MessageBox.ShowWarningAsync("Export Conversation", $"Could not write the file.\n\n{ex.Message}");
        }
        catch (Exception ex)
        {
            DebugLog.Error("Export conversation failed.", ex);
            await VS.MessageBox.ShowErrorAsync("Export Conversation", $"Could not write the file.\n\n{ex.Message}");
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

        // Reflect the default agent applied during load (Settings → Agents → Make Default).
        var agent = _vm.CurrentAgent;
        if (!string.IsNullOrEmpty(agent))
            AgentCombo.SelectedValue = agent;
    }

    private bool _scrollPending;
        private ScrollViewer? _scrollViewer;

        /// <summary>
        /// Auto-scrolls to bottom ONLY if the user is already near the bottom of the transcript.
        /// If the user scrolled up to read earlier content, they stay put instead of being yanked
        /// to the bottom on every token delta. The threshold (50 px) is generous enough that a
        /// user tracking the latest content stays pinned through minor height changes.
        /// </summary>
        private void RequestScroll()
        {
            if (_scrollPending) return;
            _scrollPending = true;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                _scrollPending = false;
                // L12: a cached ScrollViewer goes stale when the ListBox template is reapplied;
                // re-resolve whenever the cached one is no longer in MessageList's visual tree.
                if (_scrollViewer is null || !IsDescendant(MessageList, _scrollViewer))
                    _scrollViewer = FindVisualChild<ScrollViewer>(MessageList);
                if (_scrollViewer is null) return;

                // If the user is within 50 px of the bottom, they're "pinned" — auto-scroll.
                // Otherwise they've intentionally scrolled up and we leave them alone.
                double offsetFromBottom = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset;
                if (offsetFromBottom <= 50.0)
                    _scrollViewer.ScrollToBottom();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>L12: true when <paramref name="node"/> is currently a visual descendant of <paramref name="ancestor"/>.</summary>
        private static bool IsDescendant(DependencyObject ancestor, DependencyObject node)
        {
            for (var p = node; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                if (ReferenceEquals(p, ancestor))
                    return true;
            return false;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                    return found;
                var descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                    return descendant;
            }
            return null;
        }
    #pragma warning restore VSTHRD001, VSTHRD110

    /// <summary>Click on the context progress bar requests compaction.</summary>
    private async void ContextBar_Click(object sender, MouseButtonEventArgs e)
    {
        await CompactContextAsync();
    }

    /// <summary>Click on the Compact button requests compaction.</summary>
    private async void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        await CompactContextAsync();
    }

    /// <summary>Sends a compaction request to the model, replaces history with the summary.</summary>
    private async System.Threading.Tasks.Task CompactContextAsync()
    {
        if (_vm is null || _vm.IsBusy) return;

        // T4: confirm with a VS-themed, async message box (the plain WPF MessageBox ignores the
        // VS theme and blocks the UI thread).
        bool confirmed = await VS.MessageBox.ShowConfirmAsync(
            "Compact Context",
            "Request the model to summarize the conversation to reduce context usage?");

        if (!confirmed) return;

        try
        {
            await _vm.CompactAsync();
        }
        catch (Exception ex)
        {
            // Callers are async void WPF handlers: an unhandled exception here would crash devenv.
            DebugLog.Error("Context compaction failed.", ex);
            await VS.MessageBox.ShowErrorAsync("Error", $"Error compacting context: {ex.Message}");
        }
    }
}
