using Kaeo.LlmProxy.VSExtension.Core;
using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// The extension's settings window. Manages Ollama connections and their models in a
    /// single "Models" tab. Every change auto-saves to the shared <see cref="ExtensionSettingsStore"/>
    /// (no explicit Save button); the built-in VS OK/Cancel command bar simply closes the window.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly ExtensionSettingsStore _store;
        private readonly McpServerManager _mcpManager;
        private readonly ObservableCollection<ConnectionViewModel> _connections = new();
        private readonly ObservableCollection<AgentViewModel> _agents = new();
        private readonly ObservableCollection<McpServerViewModel> _mcpServers = new();
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
            _ = LoadAsync();
        }

        /// <summary>Loads settings and populates the connection list.</summary>
        private async Task LoadAsync()
        {
            try
            {
                _settings = await _store.LoadAsync();

                foreach (var c in _settings.Connections ?? Array.Empty<Connection>())
                {
                    var vm = new ConnectionViewModel
                    {
                        Name = c.Name ?? string.Empty,
                        Url = c.BaseUrl ?? string.Empty,
                        ApiKey = c.ApiKey ?? string.Empty,
                        Enabled = c.Enabled,
                        Upstream = string.IsNullOrWhiteSpace(c.Upstream) ? "Ollama" : c.Upstream,
                        IsExpanded = true,
                    };
                    foreach (var m in c.Models ?? Array.Empty<ModelEntry>())
                    {
                        vm.Models.Add(new ModelViewModel
                        {
                            Name = m.Name ?? string.Empty,
                            ModelId = m.Name ?? string.Empty,
                            Capabilities = m.Capabilities != null ? string.Join(", ", m.Capabilities) : string.Empty,
                            Enabled = m.Enabled,
                            IsPinned = m.Pinned,
                        });
                    }
                    WireForSave(vm);
                    _connections.Add(vm);
                }

                var savedAgents = _settings.Agents ?? Array.Empty<Agent>();
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

                foreach (var m in _settings.McpServers ?? Array.Empty<McpServer>())
                {
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
                    foreach (var t in m.Tools ?? Array.Empty<McpTool>())
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
        }

        /// <summary>
        /// Pulls the server's tool list live and repopulates the grid. The pull is not persisted by
        /// the manager: this window owns the settings object, so it saves the merged result itself.
        /// </summary>
        private async void RefreshMcpTools_Click(object sender, RoutedEventArgs e)
        {
            if (McpServersList.SelectedItem is not McpServerViewModel server)
                return;

            server.IsRefreshing = true;
            server.LastError = null;
            try
            {
                var tools = await _mcpManager.FetchToolsAsync(MapToMcpServer(server));
                server.ReplaceTools(tools);
                server.LastSyncUtc = DateTime.UtcNow;
                SaveNow();
                RaiseModelsChanged();
            }
            catch (Exception ex)
            {
                server.LastError = ex.Message;
            }
            finally
            {
                server.IsRefreshing = false;
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
                Tools = s.Tools.Select(t => new McpTool
                {
                    Name = t.Name,
                    Description = t.Description,
                    Schema = t.Schema?.DeepClone(),
                    Enabled = t.Enabled,
                }).ToArray(),
            };
        }

        /// <summary>Schedules a debounced save (500 ms) to coalesce rapid edits.</summary>
        private void ScheduleSave()
        {
            _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Stop();
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
            _saveTimer.Start();
        }

        /// <summary>Persists the current state and notifies the tool window on success.</summary>
        private void SaveNow()
        {
            if (!_loaded)
                return;

            _settings.Connections = _connections.Select(MapToConnection).ToArray();
            _settings.McpServers = _mcpServers.Select(MapToMcpServer).ToArray();
            PersistAsync();
        }

        /// <summary>Persists the agent list (structural changes and explicit Save clicks).</summary>
        private void SaveAgentsNow()
        {
            if (!_loaded)
                return;

            // Built-ins are only persisted as an override when they differ from their defaults.
            _settings.Agents = _agents
                .Where(a => !a.IsBuiltin || a.Description != a.DefaultDescription || a.SystemPrompt != a.DefaultSystemPrompt)
                .Select(MapToAgent)
                .ToArray();
            PersistAsync();
        }

        private void PersistAsync()
        {
            _ = _store.SaveAsync(_settings).ContinueWith(t =>
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
                Models = c.Models.Select(m => new ModelEntry
                {
                    Name = m.Name,
                    Capabilities = m.Capabilities
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToArray(),
                    Pinned = m.IsPinned,
                    Enabled = m.Enabled,
                }).ToArray(),
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
            var agent = new AgentViewModel { Name = $"Agent {_agents.Count + 1}" };
            _agents.Add(agent);
            AgentsList.SelectedItem = agent;
            SaveAgentsNow();
        }

        /// <summary>Copies the selected agent, including its prompt, under a new name.</summary>
        private void DuplicateAgent_Click(object sender, RoutedEventArgs e)
        {
            if (AgentsList.SelectedItem is not AgentViewModel source) return;

            var copy = new AgentViewModel
            {
                Name = $"{source.Name} (copy)",
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
            SaveAgentsNow();
        }

        /// <summary>Commits the editor text into the selected agent and persists the list.</summary>
        private void SaveAgent_Click(object sender, RoutedEventArgs e)
        {
            if (_editingAgent is null) return;

            if (!_editingAgent.IsBuiltin && !string.IsNullOrWhiteSpace(AgentNameBox.Text))
                _editingAgent.Name = AgentNameBox.Text.Trim();
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

        /// <summary>Notifies subscribers (the tool window) that the model set changed.</summary>
        protected virtual void RaiseModelsChanged()
        {
            ModelsChanged?.Invoke();
        }
    }
}
