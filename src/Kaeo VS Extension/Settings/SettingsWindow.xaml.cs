using Kaeo.LlmProxy.VSExtension.Core;
using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// The extension's settings window. Manages Ollama connections and their models in a
    /// single "Models" tab. Every change auto-saves to the shared <see cref="ExtensionSettingsStore"/>
    /// (no explicit Save button); the built-in VS OK/Cancel command bar simply closes the window.
    /// Inherits from <see cref="DialogWindow"/> so the title bar and chrome match the VS theme.
    /// </summary>
    public partial class SettingsWindow : DialogWindow
    {
        private readonly ExtensionSettingsStore _store;
        private readonly McpServerManager _mcpManager;
        private readonly ObservableCollection<ConnectionViewModel> _connections = [];
        private readonly ObservableCollection<AgentViewModel> _agents = [];
        private readonly ObservableCollection<McpServerViewModel> _mcpServers = [];
        private readonly ObservableCollection<InstructionEntry> _instructions = [];
        private AgentViewModel? _editingAgent;
        private bool _loadingEditor;
        private ExtensionSettings _settings = new();
        private DispatcherTimer? _saveTimer;
        private bool _loaded;
        private bool _saveErrorReported;

        /// <summary>Raised after a settings change has been persisted, so the tool window can refresh its model list.</summary>
        public event Action? ModelsChanged;

        /// <summary>Connections shown in the Models tab.</summary>
        public ObservableCollection<ConnectionViewModel> Connections => _connections;

        /// <summary>Agents shown in the Agents tab.</summary>
        public ObservableCollection<AgentViewModel> Agents => _agents;

        /// <summary>MCP servers shown in the MCP tab.</summary>
        public ObservableCollection<McpServerViewModel> McpServers => _mcpServers;

        /// <summary>Instruction files shown in the Instructions tab.</summary>
        public ObservableCollection<InstructionEntry> Instructions => _instructions;

        /// <summary>Creates a window that owns its own settings store.</summary>
        public SettingsWindow() : this(new ExtensionSettingsStore())
        {
        }

        /// <summary>Creates a window backed by the given settings store (shared with the tool window).</summary>
        internal SettingsWindow(ExtensionSettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _mcpManager = new McpServerManager(_store);
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                // The VS theme resource keys only resolve inside the devenv process; if one fails
                // to load we want a clear message to report, not a silent/unhandled crash.
                ReportError("initializing the settings window", ex);
                throw;
            }

            DataContext = this;
            // The OS-drawn caption stays light in a dark VS theme unless we apply the DWM
            // immersive dark-mode attribute; keep it in sync with the VS scheme.
            ToolWindow.WindowCaptionTheme.FollowVsTheme(this);
            // L2: stop the debounce timer and flush any pending edit when the window closes, so
            // closing within the 500 ms debounce window does not silently lose the last change.
            Closed += (_, _) =>
            {
                _saveTimer?.Stop();
                SaveNow();
            };
            _ = LoadAsync();
        }

        /// <summary>Loads settings and populates the connection list.</summary>
        private async Task LoadAsync()
        {
            try
            {
                _settings = await _store.LoadAsync();

                foreach (var c in _settings.Connections ?? [])
                {
                    var vm = new ConnectionViewModel
                    {
                        Name = c.Name ?? string.Empty,
                        Url = c.BaseUrl ?? string.Empty,
                        ApiKey = c.ApiKey ?? string.Empty,
                        Enabled = c.Enabled,
                        Upstream = string.IsNullOrWhiteSpace(c.Upstream) ? "Ollama" : c.Upstream!,
                        IsExpanded = true,
                    };
                    foreach (var m in c.Models ?? [])
                    {
                        vm.Models.Add(new ModelViewModel
                        {
                            Name = m.Name ?? string.Empty,
                            ModelId = m.Name ?? string.Empty,
                            Capabilities = m.Capabilities != null ? string.Join(", ", m.Capabilities) : string.Empty,
                            Enabled = m.Enabled,
                            IsPinned = m.Pinned,
                            ReasoningSource = string.IsNullOrWhiteSpace(m.ReasoningSource) ? "Auto" : m.ReasoningSource!,
                            ContextSize = m.ContextSize,
                        });
                    }
                    WireForSave(vm);
                    _connections.Add(vm);
                }

                var savedAgents = _settings.Agents ?? [];
                foreach (var b in BuiltinAgents.All)
                {
                    var o = savedAgents.FirstOrDefault(a => a.Name == b.Name);
                    _agents.Add(new AgentViewModel
                    {
                        Name = b.Name,
                        Description = o?.Description ?? b.Description,
                        SystemPrompt = string.IsNullOrWhiteSpace(o?.SystemPrompt) ? b.SystemPrompt : o!.SystemPrompt!,
                        Tools = o?.Tools ?? b.Tools?.ToArray(),
                        DefaultModel = o?.DefaultModel ?? b.DefaultModel,
                        IsBuiltin = true,
                        DefaultDescription = b.Description,
                        DefaultSystemPrompt = b.SystemPrompt,
                    });
                }
                foreach (var a in savedAgents)
                {
                    if (BuiltinAgents.All.Any(b => b.Name == a.Name)) continue;
                    _agents.Add(new AgentViewModel
                    {
                        Name = a.Name ?? string.Empty,
                        Description = a.Description,
                        SystemPrompt = a.SystemPrompt ?? string.Empty,
                        Tools = a.Tools,
                        DefaultModel = a.DefaultModel,
                    });
                }

                // Mark the persisted default agent so the Agents list shows a star next to it.
                var defaultAgentName = _settings.Defaults?.Agent;
                if (!string.IsNullOrEmpty(defaultAgentName))
                {
                    var def = _agents.FirstOrDefault(a => a.Name == defaultAgentName);
                    def?.IsDefault = true;
                }

                McpServer? persistedBuiltin = null;
                foreach (var m in _settings.McpServers ?? [])
                {
                    if (string.Equals(m.Transport, McpServerViewModel.BuiltinTransport, StringComparison.OrdinalIgnoreCase))
                    {
                        persistedBuiltin = m;
                        continue;
                    }

                    var vm = new McpServerViewModel
                    {
                        Name = m.Name ?? string.Empty,
                        Transport = string.IsNullOrWhiteSpace(m.Transport) ? "http" : m.Transport!,
                        Url = m.Url ?? string.Empty,
                        ApiKey = m.ApiKey ?? string.Empty,
                        Command = m.Command ?? string.Empty,
                        Arguments = McpServerViewModel.JoinArguments(m.Args),
                        Enabled = m.Enabled,
                        LastSyncUtc = m.LastSyncUtc,
                    };
                    foreach (var t in m.Tools ?? [])
                    {
                        vm.Tools.Add(new McpToolViewModel
                        {
                            Name = t.Name ?? string.Empty,
                            Description = t.Description ?? string.Empty,
                            Schema = t.Schema,
                            Enabled = t.Enabled,
                        });
                    }
                    // Wire after loading so restoring persisted values does not schedule a save.
                    WireMcpForSave(vm);
                    _mcpServers.Add(vm);
                }

                // The built-in VS tools always appear at the top of the list, even before they have
                // ever been saved, so they are manageable like any other server.
                EnsureBuiltinVm(persistedBuiltin);

                var instructionEntries = _settings.Instructions;
                if (instructionEntries is null || instructionEntries.Length == 0)
                    instructionEntries = [.. InstructionFileLoader.DefaultEntries()];
                foreach (var i in instructionEntries)
                {
                    // Migrate legacy path-only entries (no Kind recorded) to file kind.
                    if (i.IsText && i.Content is null && !string.IsNullOrWhiteSpace(i.Path))
                        i.Kind = InstructionKind.File;
                    if (string.IsNullOrEmpty(i.Scope))
                        i.Scope = InstructionScopeKind.Solution;
                    WireInstructionForSave(i);
                    _instructions.Add(i);
                }
                // The seeded defaults are always present (disable, never delete).
                int insertAt = 0;
                foreach (var def in InstructionFileLoader.DefaultEntries())
                    if (!_instructions.Any(x => string.Equals(x.Name, def.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        WireInstructionForSave(def);
                        _instructions.Insert(insertAt++, def);
                    }
                for (int i = 0; i < _instructions.Count; i++)
                    _instructions[i].Order = i;
                // M7: resolve the solution root and seed default files off the UI thread.
                var solutionRoot = await InstructionFileLoader.GetSolutionRootPathAsync();
                await Task.Run(() => InstructionFileLoader.EnsureDefaultFiles(_instructions, solutionRoot));

                var d = _settings.Defaults ??= new Defaults();
                HeartbeatBox.Text = d.HeartbeatMinutes.ToString();
                MaxIterBox.Text = d.MaxToolIterations.ToString();
                AutoPilotBudgetBox.Text = d.AutoPilotBudget.ToString();
                TempBox.Text = d.DefaultTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AutoAttachCheck.IsChecked = d.AutoAttachContext;
                ReasoningDisplayBox.ItemsSource = ReasoningDisplayKinds.DisplayNames;
                ReasoningDisplayBox.SelectedItem = ReasoningDisplayKinds.Parse(d.ReasoningDisplay);
                ProcessingDefaultBox.SelectedIndex = string.Equals(d.ProcessingDefault, "Expanded", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                ToolsDefaultBox.SelectedIndex = string.Equals(d.ToolsDefault, "Expanded", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                ReasoningFgBox.SelectedItem = ColorOption.FromHex(ColorOption.TextPresets, d.ReasoningForeground);
                ReasoningBgBox.SelectedItem = ColorOption.FromHex(ColorOption.Presets, d.ReasoningBackground);
                UserAlignBox.SelectedIndex = string.Equals(d.UserMessageAlignment, "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                UserBgBox.SelectedItem = ColorOption.FromHex(ColorOption.Presets, d.UserMessageBackground);
                UserFgBox.SelectedItem = ColorOption.FromHex(ColorOption.TextPresets, d.UserMessageForeground);
                AgentAlignBox.SelectedIndex = string.Equals(d.AgentMessageAlignment, "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                AgentBgBox.SelectedItem = ColorOption.FromHex(ColorOption.Presets, d.AgentMessageBackground);
                AgentFgBox.SelectedItem = ColorOption.FromHex(ColorOption.TextPresets, d.AgentMessageForeground);

                // Transcript font: default sentinel + installed families; size presets.
                var fontItems = new List<string> { FontDefaultLabel };
                fontItems.AddRange(System.Windows.Media.Fonts.SystemFontFamilies
                    .Select(f => f.Source)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(d.ChatFontFamily) && !fontItems.Contains(d.ChatFontFamily!))
                    fontItems.Add(d.ChatFontFamily!);
                ChatFontBox.ItemsSource = fontItems;
                ChatFontBox.SelectedItem = string.IsNullOrWhiteSpace(d.ChatFontFamily) ? FontDefaultLabel : d.ChatFontFamily;
                var sizes = new List<double> { 10, 11, 12, 13, 14, 15, 16, 18, 20 };
                var curSize = d.ChatFontSize is > 0 ? d.ChatFontSize.Value : 13;
                if (!sizes.Contains(curSize)) sizes.Add(curSize);
                sizes.Sort();
                ChatSizeBox.ItemsSource = sizes;
                ChatSizeBox.SelectedItem = curSize;

                AlwaysAllowedList.Items.Clear();
                if (d.AlwaysAllowedTools is not null)
                {
                    foreach (var t in d.AlwaysAllowedTools)
                        if (!string.IsNullOrWhiteSpace(t))
                            AlwaysAllowedList.Items.Add(t);
                }

                _loaded = true;
            }
            catch (Exception ex)
            {
                ReportError("loading settings", ex);
            }
        }

        /// <summary>Adds a new, empty connection ready for editing.</summary>
        private void AddConnection_Click(object sender, RoutedEventArgs e)
        {
            var vm = new ConnectionViewModel
            {
                Name = $"Connection {_connections.Count + 1}",
                IsExpanded = true,
            };
            WireForSave(vm);
            _connections.Add(vm);
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>
        /// Duplicates the connection whose Copy button was clicked, including its models, so the user
        /// can tweak a copy instead of configuring a new connection from scratch. Copied models start
        /// unpinned so the single-default rule (one default across all connections) still holds.
        /// </summary>
        private void CopyConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ConnectionViewModel source })
                return;

            var copy = new ConnectionViewModel
            {
                Name = $"{source.Name} (copy)",
                Url = source.Url,
                ApiKey = source.ApiKey,
                Upstream = source.Upstream,
                Enabled = source.Enabled,
                IsExpanded = true,
            };
            foreach (var m in source.Models)
            {
                copy.Models.Add(new ModelViewModel
                {
                    Name = m.Name,
                    ModelId = m.ModelId,
                    Capabilities = m.Capabilities,
                    Enabled = m.Enabled,
                    IsPinned = false,
                    ReasoningSource = m.ReasoningSource,
                    ContextSize = m.ContextSize,
                });
            }
            WireForSave(copy);
            _connections.Add(copy);
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Removes the connection whose header Delete button was clicked.</summary>
        private void DeleteConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ConnectionViewModel conn })
                return;

            // VS-owned, theme-matched message box (the plain WPF MessageBox ignores the VS theme).
            bool confirmed = Community.VisualStudio.Toolkit.VS.MessageBox.ShowConfirm(
                "Delete Connection",
                $"Delete connection \"{conn.Name}\"?");
            if (!confirmed)
                return;

            _connections.Remove(conn);
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Refreshes the single connection whose header Refresh button was clicked.</summary>
        private async void RefreshConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ConnectionViewModel conn })
                return;

            try
            {
                await conn.RefreshAsync();
                SaveNow();
                RaiseModelsChanged();
            }
            catch (Exception ex)
            {
                ReportError($"refreshing connection \"{conn.Name}\"", ex);
            }
        }

        /// <summary>Refreshes every enabled connection in parallel.</summary>
        private async void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tasks = _connections.Where(c => c.Enabled).Select(c => c.RefreshAsync()).ToList();
                await Task.WhenAll(tasks);
                SaveNow();
                RaiseModelsChanged();
            }
            catch (Exception ex)
            {
                ReportError("refreshing connections", ex);
            }
        }

        /// <summary>
        /// Subscribes to a connection and its models so any edit triggers a debounced auto-save.
        /// Selecting a model as Default unpins its siblings.
        /// </summary>
        private void WireForSave(ConnectionViewModel conn)
        {
            conn.PropertyChanged += (_, e) => ScheduleSave();

            void WireModel(ModelViewModel m)
            {
                m.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(ModelViewModel.IsPinned) && m.IsPinned)
                        UnpinAllExcept(m);
                    ScheduleSave();
                };
            }

            // Models loaded before wiring still need edit notifications (Enabled/Default).
            foreach (var m in conn.Models) WireModel(m);

            conn.Models.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (ModelViewModel m in e.NewItems) WireModel(m);
                }
                ScheduleSave();
            };
        }

        /// <summary>Keeps Default exclusive across every connection: only one model may be the default.</summary>
        private void UnpinAllExcept(ModelViewModel pinned)
        {
            foreach (var c in _connections)
                foreach (var m in c.Models)
                    if (!ReferenceEquals(m, pinned) && m.IsPinned)
                        m.IsPinned = false;
        }

        /// <summary>
        /// Subscribes a server and its tools so any edit triggers the debounced auto-save.
        /// Wiring happens after the values are restored from settings, so loading never saves.
        /// </summary>
        private void WireMcpForSave(McpServerViewModel server)
        {
            server.PropertyChanged += (_, _) => ScheduleSave();

            void WireTool(McpToolViewModel tool) => tool.PropertyChanged += (_, ev) =>
            {
                // A single toggle leaves the Tools collection untouched, so the enabled-count
                // summary has to be re-raised by hand.
                if (ev.PropertyName == nameof(McpToolViewModel.Enabled))
                    server.RefreshToolSummary();
                ScheduleSave();
            };

            foreach (var tool in server.Tools) WireTool(tool);

            server.Tools.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is not null)
                    foreach (McpToolViewModel tool in e.NewItems) WireTool(tool);
                ScheduleSave();
            };
        }

        /// <summary>
        /// Inserts the synthetic "Built-in VS Tools" server at the top of the MCP list. Its tools come
        /// from the shipped <see cref="BuiltInVsTools"/> definitions, with the per-tool enable flags
        /// restored from the persisted entry so choices survive a restart; newly added built-in tools
        /// default to enabled. The entry is always present so the built-in tools are manageable.
        /// </summary>
        private void EnsureBuiltinVm(McpServer? persisted)
        {
            var enabledByName = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in persisted?.Tools ?? [])
                if (!string.IsNullOrWhiteSpace(t.Name))
                    enabledByName[t.Name!] = t.Enabled;

            var vm = new McpServerViewModel
            {
                Name = McpServerViewModel.BuiltinName,
                Transport = McpServerViewModel.BuiltinTransport,
                Enabled = persisted?.Enabled ?? true,
                LastSyncUtc = persisted?.LastSyncUtc ?? DateTime.UtcNow,
            };
            foreach (var tool in BuiltInVsTools.GetToolDefinitions())
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                    continue;
                vm.Tools.Add(new McpToolViewModel
                {
                    Name = tool.Name!,
                    Description = tool.Description ?? string.Empty,
                    Schema = tool.Schema,
                    Enabled = !enabledByName.TryGetValue(tool.Name!, out var enabled) || enabled,
                });
            }
            WireMcpForSave(vm);
            _mcpServers.Insert(0, vm);
        }

        /// <summary>Adds a new, empty MCP server and selects it for editing.</summary>
        private void NewMcpServer_Click(object sender, RoutedEventArgs e)
        {
            var vm = new McpServerViewModel { Name = $"MCP Server {_mcpServers.Count + 1}" };
            WireMcpForSave(vm);
            _mcpServers.Add(vm);
            McpServersList.SelectedItem = vm;
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Removes the selected MCP server after a confirmation prompt.</summary>
        private void DeleteMcpServer_Click(object sender, RoutedEventArgs e)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel vm)
                return;

            // The built-in server is synthetic and its Delete button is hidden; guard anyway.
            if (vm.IsBuiltin)
                return;

            bool confirmed = Community.VisualStudio.Toolkit.VS.MessageBox.ShowConfirm(
                "Delete MCP Server",
                $"Delete MCP server \"{vm.Name}\"?");
            if (!confirmed)
                return;

            _mcpServers.Remove(vm);
            ShowMcpEditor(McpServersList.SelectedItem as McpServerViewModel);
            SaveNow();
            RaiseModelsChanged();
        }

        private void McpServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShowMcpEditor(McpServersList.SelectedItem as McpServerViewModel);

        /// <summary>Points the editor pane at the selected server, or shows the empty-state hint.</summary>
        private void ShowMcpEditor(McpServerViewModel? server)
        {
            McpEditor.DataContext = server;
            McpEditor.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
            NoMcpServerHint.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;

            // The built-in server has no transport/URL/command to configure and cannot be deleted,
            // so hide those controls; its tool grid and enable checkboxes remain.
            bool builtin = server?.IsBuiltin == true;
            McpConnectionFields.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
            TestMcpButton.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
            SaveMcpButton.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
            DeleteMcpButton.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Pulls the server's tool list live and repopulates the grid. The pull is not persisted by
        /// the manager: this window owns the settings object, so it saves the merged result itself.
        /// </summary>
        private async void RefreshMcpTools_Click(object sender, RoutedEventArgs e)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel server)
                return;

            // The built-in tools live in-process; "refresh" just re-syncs the grid from the shipped
            // definitions so tools added in a newer build appear, keeping current enable flags.
            if (server.IsBuiltin)
            {
                server.ClearDiagnostics();
                server.ReplaceTools(BuiltInVsTools.GetToolDefinitions());
                server.LastSyncUtc = DateTime.UtcNow;
                SaveNow();
                RaiseModelsChanged();
                server.SetStatus($"Re-synced {server.Tools.Count} built-in tool(s).");
                return;
            }

            server.IsRefreshing = true;
            server.ClearDiagnostics();
            try
            {
                var tools = await _mcpManager.FetchToolsAsync(MapToMcpServer(server));
                server.ReplaceTools(tools);
                server.LastSyncUtc = DateTime.UtcNow;
                SaveNow();
                RaiseModelsChanged();
                server.SetStatus($"Pulled {tools.Count} tool(s) from \"{server.Name}\".");
                DebugLog.Info($"MCP tools pulled for '{server.Name}': {tools.Count}.");
            }
            catch (Exception ex)
            {
                server.SetError(ex);
                DebugLog.Error($"Pulling MCP tools for '{server.Name}' failed.", ex);
            }
            finally
            {
                server.IsRefreshing = false;
            }
        }

        /// <summary>Probes the selected server and reports the outcome inline (hover or click an error for detail).</summary>
        private async void TestMcpConnection_Click(object sender, RoutedEventArgs e)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel server)
                return;

            server.IsRefreshing = true;
            server.SetStatus("Testing connection...");
            try
            {
                var summary = await _mcpManager.TestConnectionAsync(MapToMcpServer(server));
                server.SetStatus(summary);
            }
            catch (Exception ex)
            {
                server.SetError(ex);
                DebugLog.Error($"MCP connectivity test for '{server.Name}' failed.", ex);
            }
            finally
            {
                server.IsRefreshing = false;
            }
        }

        /// <summary>
        /// Persists the selected server immediately instead of waiting for the debounce, and reports
        /// the result inline. If the write itself fails, the existing save-error dialog still surfaces it.
        /// </summary>
        private void SaveMcpServer_Click(object sender, RoutedEventArgs e)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel server)
                return;

            _saveTimer?.Stop();
            SaveNow();
            RaiseModelsChanged();
            server.SetStatus($"Saved \"{server.Name}\" ({server.Tools.Count} tool(s)).");
            DebugLog.Info($"MCP server '{server.Name}' saved (transport={server.Transport}, tools={server.Tools.Count}).");
        }

        /// <summary>
        /// Copies the full error for the selected server - message, inner chain and stack trace - to
        /// the clipboard, so a truncated inline line can still be shared in full.
        /// </summary>
        private void McpError_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is not TextBlock block)
                return;

            var text = (block.DataContext as McpServerViewModel)?.LastErrorDetail ?? block.Text;
            if (string.IsNullOrEmpty(text))
                return;

            try
            {
                System.Windows.Clipboard.SetText(text);
                if (block.DataContext is McpServerViewModel server)
                    server.StatusMessage = "Error copied to the clipboard.";
                DebugLog.Verbose("Copied an MCP error with its stack trace to the clipboard.");
            }
            catch (Exception ex)
            {
                // The clipboard can be held by another process; log rather than replace the error the user is reading.
                DebugLog.Error("Copying the MCP error to the clipboard failed.", ex);
            }
        }

        private void EnableAllTools_Click(object sender, RoutedEventArgs e) => SetAllToolsEnabled(true);

        private void DisableAllTools_Click(object sender, RoutedEventArgs e) => SetAllToolsEnabled(false);

        /// <summary>Bulk-toggles every tool of the selected server.</summary>
        private void SetAllToolsEnabled(bool enabled)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel server)
                return;

            foreach (var tool in server.Tools)
                tool.Enabled = enabled;

            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Maps an MCP server view model back to the persisted <see cref="McpServer"/> shape.</summary>
        private static McpServer MapToMcpServer(McpServerViewModel s)
        {
            return new McpServer
            {
                Name = s.Name,
                Transport = s.Transport,
                Url = s.Url,
                ApiKey = s.ApiKey,
                Command = s.Command,
                Args = McpServerViewModel.SplitArguments(s.Arguments),
                Enabled = s.Enabled,
                LastSyncUtc = s.LastSyncUtc,
                // DeepClone: a JsonNode cannot be parented by both the live view model and the
                // settings object being serialized.
                Tools = [.. s.Tools.Select(t => new McpTool
                {
                    Name = t.Name,
                    Description = t.Description,
                    Schema = t.Schema?.DeepClone(),
                    Enabled = t.Enabled,
                })],
            };
        }

        /// <summary>Schedules a debounced save (500 ms) to coalesce rapid edits.</summary>
        private void ScheduleSave()
        {
            if (_saveTimer is null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _saveTimer.Tick += (_, _) =>
                {
                    _saveTimer.Stop();
                    SaveNow();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Persists the current state and notifies the tool window on success.</summary>
        private void SaveNow()
        {
            if (!_loaded)
                return;

            _settings.Connections = [.. _connections.Select(MapToConnection)];
            _settings.McpServers = [.. _mcpServers.Select(MapToMcpServer)];
            _settings.Instructions = [.. _instructions.Select(MapToInstructionEntry)];
            // Keep the always-allowed list in sync on every save path: Remove/Clear only
            // schedule a debounced save, so without this their changes would never persist.
            _settings.Defaults ??= new Defaults();
            _settings.Defaults.AlwaysAllowedTools = [.. AlwaysAllowedList.Items.Cast<string>().Where(s => !string.IsNullOrWhiteSpace(s))];
            PersistSave();
        }

        // ── Instructions Tab ──────────────────────────────────────────────────

        /// <summary>Wires an instruction entry so edits auto-persist (debounced), like the MCP tools.</summary>
        private void WireInstructionForSave(InstructionEntry entry)
            => entry.PropertyChanged += (_, _) => ScheduleSave();

        /// <summary>Adds a new inline-text instruction and selects it.</summary>
        private void NewTextInstruction_Click(object sender, RoutedEventArgs e)
        {
            var entry = new InstructionEntry
            {
                Name = "New Instruction",
                Kind = InstructionKind.Text,
                Content = string.Empty,
                Enabled = true,
                Order = _instructions.Count,
            };
            WireInstructionForSave(entry);
            _instructions.Add(entry);
            InstructionsList.SelectedItem = entry;
            SaveNow();
        }

        /// <summary>Adds a new file-backed instruction and selects it.</summary>
        private void NewFileInstruction_Click(object sender, RoutedEventArgs e)
        {
            var entry = new InstructionEntry
            {
                Name = "New File Instruction",
                Kind = InstructionKind.File,
                Path = string.Empty,
                Enabled = true,
                Order = _instructions.Count,
            };
            WireInstructionForSave(entry);
            _instructions.Add(entry);
            InstructionsList.SelectedItem = entry;
            SaveNow();
        }

        /// <summary>Removes the selected instruction and re-orders the rest.</summary>
        private void DeleteInstruction_Click(object sender, RoutedEventArgs e)
        {
            if (InstructionsList.SelectedItem is not InstructionEntry entry)
                return;

            // Seeded defaults can be disabled but not deleted.
            if (entry.IsDefault)
                return;

            _instructions.Remove(entry);
            for (int i = 0; i < _instructions.Count; i++)
                _instructions[i].Order = i;
            ShowInstructionEditor(InstructionsList.SelectedItem as InstructionEntry);
            SaveNow();
        }

        /// <summary>Moves the selected instruction up one position.</summary>
        private void MoveInstructionUp_Click(object sender, RoutedEventArgs e)
        {
            if (InstructionsList.SelectedItem is not InstructionEntry entry)
                return;

            int idx = _instructions.IndexOf(entry);
            if (idx > 0)
            {
                _instructions.RemoveAt(idx);
                _instructions.Insert(idx - 1, entry);
                ReorderInstructions();
                SaveNow();
            }
        }

        /// <summary>Moves the selected instruction down one position.</summary>
        private void MoveInstructionDown_Click(object sender, RoutedEventArgs e)
        {
            if (InstructionsList.SelectedItem is not InstructionEntry entry)
                return;

            int idx = _instructions.IndexOf(entry);
            if (idx < _instructions.Count - 1)
            {
                _instructions.RemoveAt(idx);
                _instructions.Insert(idx + 1, entry);
                ReorderInstructions();
                SaveNow();
            }
        }

        private void ReorderInstructions()
        {
            for (int i = 0; i < _instructions.Count; i++)
                _instructions[i].Order = i;
        }

        private void InstructionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShowInstructionEditor(InstructionsList.SelectedItem as InstructionEntry);

        /// <summary>
        /// Points the instruction editor at the selected entry. For a file entry, the current on-disk
        /// content is loaded into the text area so the user edits the real file body.
        /// </summary>
        private async void ShowInstructionEditor(InstructionEntry? entry)
        {
            InstructionEditor.DataContext = entry;
            bool has = entry is not null;
            InstructionEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            NoInstructionHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

            // M7: read the file body off the UI thread, and only when the buffer is empty so
            // re-selecting an entry never clobbers unsaved edits. Guard against a stale apply if
            // the user moved to another entry while the read was in flight.
            if (entry is { IsFile: true, Content: null })
            {
                var path = entry.Path;
                var content = await Task.Run(() => InstructionFileLoader.ReadFileContent(path));
                if (ReferenceEquals(InstructionEditor.DataContext, entry))
                    entry.Content = content;
            }
        }

        /// <summary>Browses for a Markdown/text file and loads its content into the editor.</summary>
        private async void BrowseInstructionFile_Click(object sender, RoutedEventArgs e)
        {
            if (InstructionEditor.DataContext is not InstructionEntry entry)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose an instruction file",
                Filter = "Markdown / Text (*.md;*.txt)|*.md;*.txt|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true)
                return;

            entry.Path = await MakeRelativeIfUnderSolutionAsync(dialog.FileName);
            var path = entry.Path;
            entry.Content = await Task.Run(() => InstructionFileLoader.ReadFileContent(path));
            SaveNow();
        }

        /// <summary>
        /// Saves the selected instruction: file entries write their body back to disk; text entries
        /// keep it inline. Then persists the list and notifies the tool window.
        /// </summary>
        private void SaveInstruction_Click(object sender, RoutedEventArgs e)
        {
            if (InstructionEditor.DataContext is not InstructionEntry entry)
                return;

            if (entry.IsFile)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    Community.VisualStudio.Toolkit.VS.MessageBox.ShowError(
                        "Instruction", "Set a file path before saving a file-based instruction.");
                    return;
                }
                try
                {
                    InstructionFileLoader.WriteFileContent(entry.Path, entry.Content);
                }
                catch (Exception ex)
                {
                    Community.VisualStudio.Toolkit.VS.MessageBox.ShowError(
                        "Instruction", $"Could not write the file.\n\n{ex.Message}");
                    return;
                }
            }

            _saveTimer?.Stop();
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Converts an absolute path under the solution root into a solution-relative one.</summary>
        private static async Task<string> MakeRelativeIfUnderSolutionAsync(string absolutePath)
        {
            var root = await InstructionFileLoader.GetSolutionRootPathAsync();
            if (string.IsNullOrEmpty(root))
                return absolutePath;
            try
            {
                var trimmed = root!.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()) ? root : root + System.IO.Path.DirectorySeparatorChar;
                var rootUri = new Uri(trimmed);
                var fileUri = new Uri(absolutePath);
                if (rootUri.IsBaseOf(fileUri))
                    return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString())
                        .Replace('/', System.IO.Path.DirectorySeparatorChar);
            }
            catch
            {
                // Fall through to the absolute path.
            }
            return absolutePath;
        }

        /// <summary>Maps a live instruction entry to its persisted shape (file bodies are never stored inline).</summary>
        private static InstructionEntry MapToInstructionEntry(InstructionEntry e)
        {
            bool isFile = string.Equals(e.Kind, InstructionKind.File, StringComparison.OrdinalIgnoreCase);
            return new InstructionEntry
            {
                Name = e.Name,
                Kind = e.Kind,
                Path = isFile ? e.Path : null,
                Content = isFile ? null : e.Content,
                Enabled = e.Enabled,
                Order = e.Order,
                Scope = e.Scope,
                IsDefault = e.IsDefault,
            };
        }

        /// <summary>Parses the Preferences controls into the shared Defaults and persists them.</summary>
        private const string FontDefaultLabel = "Default (Cascadia / Consolas)";

        private void SavePreferences_Click(object sender, RoutedEventArgs e)
        {
            var d = _settings.Defaults ??= new Defaults();
            if (int.TryParse(HeartbeatBox.Text, out var hb) && hb >= 1)
                d.HeartbeatMinutes = hb;
            if (int.TryParse(MaxIterBox.Text, out var mi) && mi >= 1)
                d.MaxToolIterations = mi;
            if (int.TryParse(AutoPilotBudgetBox.Text, out var ab) && ab >= 1)
                d.AutoPilotBudget = ab;
            if (double.TryParse(TempBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temp) && temp >= 0 && temp <= 2)
                d.DefaultTemperature = temp;
            d.AutoAttachContext = AutoAttachCheck.IsChecked == true;
            d.ReasoningDisplay = ReasoningDisplayKinds.Parse(ReasoningDisplayBox.SelectedItem as string);
            d.ProcessingDefault = (ProcessingDefaultBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Collapsed";
            d.ToolsDefault = (ToolsDefaultBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Collapsed";
            d.ReasoningForeground = (ReasoningFgBox.SelectedItem as ColorOption)?.Hex;
            d.ReasoningBackground = (ReasoningBgBox.SelectedItem as ColorOption)?.Hex;
            d.UserMessageAlignment = (UserAlignBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Left";
            d.UserMessageBackground = (UserBgBox.SelectedItem as ColorOption)?.Hex;
            d.UserMessageForeground = (UserFgBox.SelectedItem as ColorOption)?.Hex;
            d.AgentMessageAlignment = (AgentAlignBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Left";
            d.AgentMessageBackground = (AgentBgBox.SelectedItem as ColorOption)?.Hex;
            d.AgentMessageForeground = (AgentFgBox.SelectedItem as ColorOption)?.Hex;
            d.ChatFontFamily = (ChatFontBox.SelectedItem as string) == FontDefaultLabel ? null : ChatFontBox.SelectedItem as string;
            d.ChatFontSize = ChatSizeBox.SelectedItem is double fs ? fs : (double?)null;
            d.AlwaysAllowedTools = [.. AlwaysAllowedList.Items.Cast<string>().Where(s => !string.IsNullOrWhiteSpace(s))];

            _saveTimer?.Stop();
            SaveNow();
            RaiseModelsChanged();
        }

        /// <summary>Removes the selected tool from the always-allowed list and persists.</summary>
        private void RemoveAlwaysAllowed_Click(object sender, RoutedEventArgs e)
        {
            if (AlwaysAllowedList.SelectedItem is string tool && !string.IsNullOrWhiteSpace(tool))
                AlwaysAllowedList.Items.Remove(tool);
            ScheduleSave();
        }

        /// <summary>Clears all always-allowed tools and persists.</summary>
        private void ClearAlwaysAllowed_Click(object sender, RoutedEventArgs e)
        {
            AlwaysAllowedList.Items.Clear();
            ScheduleSave();
        }

        /// <summary>Persists the agent list (structural changes and explicit Save clicks).</summary>
        private void SaveAgentsNow()
        {
            if (!_loaded)
                return;

            // Built-ins are only persisted as an override when they differ from their defaults.
            _settings.Agents = [.. _agents
                .Where(a => !a.IsBuiltin || a.Description != a.DefaultDescription || a.SystemPrompt != a.DefaultSystemPrompt)
                .Select(MapToAgent)];
            PersistSave();
        }

        private void PersistSave()
        {
            // M1: merge the dialog-owned sections into a freshly-loaded snapshot under the store's
            // write lock instead of overwriting the whole file with the snapshot captured at open.
            // Fields owned by concurrent writers (notably Defaults.Model/Mode, set by the tool
            // window) are preserved because the dialog never edits them.
            var connections = _settings.Connections;
            var mcpServers = _settings.McpServers;
            var instructions = _settings.Instructions;
            var agents = _settings.Agents;
            var edited = _settings.Defaults;

            _ = _store.UpdateAsync(current =>
            {
                current.Connections = connections;
                current.McpServers = mcpServers;
                current.Instructions = instructions;
                current.Agents = agents;
                var d = current.Defaults ??= new Defaults();
                if (edited is not null)
                {
                    d.Agent = edited.Agent;
                    d.AutoAttachContext = edited.AutoAttachContext;
                    d.DefaultTemperature = edited.DefaultTemperature;
                    d.HeartbeatMinutes = edited.HeartbeatMinutes;
                    d.MaxToolIterations = edited.MaxToolIterations;
                    d.AutoPilotBudget = edited.AutoPilotBudget;
                    d.ProcessingDefault = edited.ProcessingDefault;
                    d.ReasoningDisplay = edited.ReasoningDisplay;
                    d.ToolsDefault = edited.ToolsDefault;
                    d.AlwaysAllowedTools = edited.AlwaysAllowedTools;
                    d.ReasoningForeground = edited.ReasoningForeground;
                    d.ReasoningBackground = edited.ReasoningBackground;
                    d.UserMessageAlignment = edited.UserMessageAlignment;
                    d.UserMessageBackground = edited.UserMessageBackground;
                    d.UserMessageForeground = edited.UserMessageForeground;
                    d.AgentMessageAlignment = edited.AgentMessageAlignment;
                    d.AgentMessageBackground = edited.AgentMessageBackground;
                    d.AgentMessageForeground = edited.AgentMessageForeground;
                    d.ChatFontFamily = edited.ChatFontFamily;
                    d.ChatFontSize = edited.ChatFontSize;
                    // Model and Mode are owned by the tool window; keep the freshly-loaded values.
                }
                return Task.CompletedTask;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (!_saveErrorReported)
                    {
                        _saveErrorReported = true;
                        ReportError("saving settings", t.Exception);
                    }
                }
                else if (!t.IsCanceled)
                {
                    _saveErrorReported = false;
                    RaiseModelsChanged();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Shows a VS-themed, parented error dialog with the operation that failed and the
        /// underlying reason, so problems are visible to the user instead of silently swallowed.
        /// </summary>
        private static void ReportError(string operation, Exception? ex)
        {
            var detail = FlattenException(ex);
            DebugLog.Error($"There was a problem {operation}.", ex);
            Community.VisualStudio.Toolkit.VS.MessageBox.ShowError(
                "Kaeo Settings",
                $"There was a problem {operation}.\n\n{detail}");
        }

        /// <summary>Collapses aggregate/inner exception chains into a readable multi-line message.</summary>
        private static string FlattenException(Exception? ex)
        {
            var seen = new System.Collections.Generic.HashSet<Exception>();
            var sb = new System.Text.StringBuilder();
            FlattenInto(ex, sb, seen);
            return sb.Length > 0 ? sb.ToString() : "Unknown error.";
        }

        private static void FlattenInto(Exception? ex, System.Text.StringBuilder sb, System.Collections.Generic.HashSet<Exception> seen)
        {
            if (ex == null || !seen.Add(ex))
                return;

            if (sb.Length > 0)
                sb.AppendLine();

            if (ex is AggregateException agg)
            {
                sb.Append($"{agg.GetType().Name}: {agg.Message}");
                foreach (var inner in agg.InnerExceptions)
                    FlattenInto(inner, sb, seen);
                return;
            }

            if (ex.InnerException != null)
                sb.Append($"{ex.GetType().Name}: ");
            sb.Append(ex.Message);
            FlattenInto(ex.InnerException, sb, seen);
        }

        /// <summary>Maps a connection view model back to the persisted <see cref="Connection"/> shape.</summary>
        private static Connection MapToConnection(ConnectionViewModel c)
        {
            return new Connection
            {
                Name = c.Name,
                BaseUrl = c.Url,
                ApiKey = c.ApiKey,
                Enabled = c.Enabled,
                Upstream = c.Upstream,
                Models = [.. c.Models.Select(m => new ModelEntry
                {
                    Name = m.Name,
                    Capabilities = [.. m.Capabilities
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)],
                    ContextSize = m.ContextSize,
                    Pinned = m.IsPinned,
                    Enabled = m.Enabled,
                    ReasoningSource = string.Equals(m.ReasoningSource, "Auto", StringComparison.Ordinal) ? null : m.ReasoningSource,
                })],
            };
        }

        /// <summary>Loads the selected agent into the editor and clears the dirty state.</summary>
        private void AgentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAgentEditor(AgentsList.SelectedItem as AgentViewModel);
        }

        private void LoadAgentEditor(AgentViewModel? agent)
        {
            _editingAgent = agent;
            _loadingEditor = true;
            AgentNameBox.Text = agent?.Name ?? string.Empty;
            AgentDescriptionBox.Text = agent?.Description ?? string.Empty;
            AgentPromptBox.Text = agent?.SystemPrompt ?? string.Empty;
            _loadingEditor = false;
            AgentNameBox.IsReadOnly = agent?.IsBuiltin == true;
            RevertAgentButton.IsEnabled = agent?.IsBuiltin == true;
            SaveAgentButton.IsEnabled = false;
        }

        /// <summary>Enables Save once the user edits any editor field for the selected agent.</summary>
        private void AgentEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingEditor) return;
            SaveAgentButton.IsEnabled = _editingAgent is not null;
        }

        /// <summary>Creates a new empty agent and selects it for editing.</summary>
        private void NewAgent_Click(object sender, RoutedEventArgs e)
        {
            var agent = new AgentViewModel { Name = UniqueAgentName($"Agent {_agents.Count + 1}") };
            _agents.Add(agent);
            AgentsList.SelectedItem = agent;
            SaveAgentsNow();
        }

        /// <summary>L10: returns a name not already used by another agent (case-insensitive).</summary>
        private string UniqueAgentName(string baseName, AgentViewModel? exclude = null)
        {
            var name = baseName;
            var n = 2;
            while (_agents.Any(a => !ReferenceEquals(a, exclude)
                && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{baseName} ({n++})";
            }
            return name;
        }

        /// <summary>Copies the selected agent, including its prompt, under a new name.</summary>
        private void DuplicateAgent_Click(object sender, RoutedEventArgs e)
        {
            if (AgentsList.SelectedItem is not AgentViewModel source) return;

            var copy = new AgentViewModel
            {
                Name = UniqueAgentName($"{source.Name} (copy)"),
                Description = source.Description,
                SystemPrompt = source.SystemPrompt,
                Tools = source.Tools,
                DefaultModel = source.DefaultModel,
            };
            _agents.Add(copy);
            AgentsList.SelectedItem = copy;
            SaveAgentsNow();
        }

        /// <summary>Deletes the selected agent after a confirmation prompt.</summary>
        private void DeleteAgent_Click(object sender, RoutedEventArgs e)
        {
            if (AgentsList.SelectedItem is not AgentViewModel agent) return;

            bool confirmed = Community.VisualStudio.Toolkit.VS.MessageBox.ShowConfirm(
                "Delete Agent",
                $"Delete agent \"{agent.Name}\"?");
            if (!confirmed) return;

            _agents.Remove(agent);
            if (ReferenceEquals(_editingAgent, agent))
                LoadAgentEditor(null);
            // L10: if the default agent was deleted, repoint Defaults.Agent at a remaining agent.
            if (_settings.Defaults is not null
                && string.Equals(_settings.Defaults.Agent, agent.Name, StringComparison.OrdinalIgnoreCase))
                _settings.Defaults.Agent = _agents.FirstOrDefault()?.Name ?? string.Empty;
            SaveAgentsNow();
        }

        /// <summary>Commits the editor text into the selected agent and persists the list.</summary>
        private void SaveAgent_Click(object sender, RoutedEventArgs e)
        {
            if (_editingAgent is null) return;

            if (!_editingAgent.IsBuiltin && !string.IsNullOrWhiteSpace(AgentNameBox.Text))
            {
                // L10: keep names unique and let the default-agent pointer follow a rename.
                var oldName = _editingAgent.Name;
                var newName = UniqueAgentName(AgentNameBox.Text.Trim(), _editingAgent);
                _editingAgent.Name = newName;
                if (_settings.Defaults is not null
                    && string.Equals(_settings.Defaults.Agent, oldName, StringComparison.OrdinalIgnoreCase))
                    _settings.Defaults.Agent = newName;
            }
            _editingAgent.Description = string.IsNullOrWhiteSpace(AgentDescriptionBox.Text) ? null : AgentDescriptionBox.Text;
            _editingAgent.SystemPrompt = AgentPromptBox.Text;
            SaveAgentsNow();
            LoadAgentEditor(_editingAgent);
        }

        /// <summary>Restores a built-in agent's description/prompt to the shipped defaults.</summary>
        private void RevertAgent_Click(object sender, RoutedEventArgs e)
        {
            if (_editingAgent is not { IsBuiltin: true } agent) return;

            agent.Description = agent.DefaultDescription;
            agent.SystemPrompt = agent.DefaultSystemPrompt ?? string.Empty;
            SaveAgentsNow();
            LoadAgentEditor(agent);
        }

        /// <summary>Marks the selected agent as the default the chat window starts on and persists it.</summary>
        private void MakeDefaultAgent_Click(object sender, RoutedEventArgs e)
        {
            var agent = _editingAgent ?? AgentsList.SelectedItem as AgentViewModel;
            if (agent is null)
                return;

            foreach (var a in _agents)
                a.IsDefault = ReferenceEquals(a, agent);

            _settings.Defaults ??= new Defaults();
            _settings.Defaults.Agent = agent.Name;
            SaveAgentsNow();
        }

        /// <summary>Maps an agent view model back to the persisted <see cref="Agent"/> shape.</summary>
        private static Agent MapToAgent(AgentViewModel a)
        {
            return new Agent
            {
                Name = a.Name,
                Description = a.Description,
                SystemPrompt = a.SystemPrompt,
                Tools = a.Tools,
                DefaultModel = a.DefaultModel,
            };
        }

        /// <summary>
        /// Closes the window. Everything auto-saves as it is edited, so there is nothing to confirm;
        /// the button exists because the VS command bar's OK/Cancel is not obvious in this dialog.
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Notifies subscribers (the tool window) that the model set changed.</summary>
        protected virtual void RaiseModelsChanged()
        {
            ModelsChanged?.Invoke();
        }
    }
}
