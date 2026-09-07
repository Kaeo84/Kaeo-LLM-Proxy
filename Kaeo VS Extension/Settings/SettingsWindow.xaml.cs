using Kaeo.LlmProxy.VSExtension.Core;
using Microsoft.VisualStudio.PlatformUI;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    public partial class SettingsWindow : DialogWindow
    {
        private readonly ExtensionSettingsStore _settings;

        public event Action? ModelsChanged;

        internal SettingsWindow(ExtensionSettingsStore settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            // Ensure VS theme dictionaries and dialog styles are merged before the visual tree is created.
            // This must run before InitializeComponent so DynamicResource lookups resolve to VS brushes.
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true);

            InitializeComponent();

            // Kick off async load (fire-and-forget is fine here; exceptions are handled inside the method)
            _ = LoadModelsTabAsync();

            // Designer-safe: avoid runtime-only calls while in the XAML designer
            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                // Safe to call again at runtime; ensures styles are applied if anything changed.
                ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            // If the window was shown modally (ShowDialog), setting DialogResult ensures the dialog closes
            // on the first click. Only set DialogResult when there is an owner (modal scenario).
            if (Owner != null)
            {
                DialogResult = true;
            }

            Close();
        }

        public void OpenTab(string tabName)
        {
            MainTabControl.SelectedItem = tabName switch
            {
                "General" => TabGeneral,
                "Models" => TabModels,
                "Agents" => TabAgents,
                "MCP" => TabMcp,
                _ => TabGeneral
            };
        }

        private async Task LoadModelsTabAsync()
        {
            try
            {
                var s = await _settings.LoadAsync();
                ConnectionsTree.Items.Clear();

                foreach (var conn in s.Connections ?? Array.Empty<Connection>())
                {
                    if (string.IsNullOrWhiteSpace(conn.BaseUrl)) continue;

                    var connNode = new TreeViewItem { Header = $"{conn.Name}  ({conn.BaseUrl})" };
                    var client = new OllamaApiClient(conn.BaseUrl, conn.ApiKey);

                    try
                    {
                        var models = await client.GetModelsAsync();
                        foreach (var m in models)
                        {
                            var flag = m.SupportsTools ? "  [tools]" : string.Empty;
                            connNode.Items.Add(new TreeViewItem { Header = $"{m.Name}{flag}" });
                        }
                    }
                    catch
                    {
                        connNode.Items.Add(new TreeViewItem { Header = "(unreachable)" });
                    }

                    ConnectionsTree.Items.Add(connNode);
                }

                ModelsStatusText.Text = s.Connections?.Any(c => c.Enabled) == true
                    ? "Models loaded from enabled connections. Tool-capable models are marked [tools]."
                    : "No connections configured. Add one above.";
            }
            catch (Exception ex)
            {
                // Surface load errors in the status text so the user sees them
                ModelsStatusText.Text = $"Error loading models: {ex.Message}";
            }
        }

        private async void AddConnButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = ConnNameBox.Text.Trim();
                var url = ConnUrlBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    ModelsStatusText.Text = "Both a name and a base URL are required.";
                    return;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    ModelsStatusText.Text = "Base URL must be a valid http(s) URL.";
                    return;
                }

                var s = await _settings.LoadAsync();
                s.Connections ??= Array.Empty<Connection>();

                if (s.Connections.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    ModelsStatusText.Text = $"Connection '{name}' already exists.";
                    return;
                }

                s.Connections = s.Connections.Append(new Connection
                {
                    Name = name,
                    BaseUrl = url,
                    ApiKey = string.IsNullOrWhiteSpace(ConnKeyBox.Text) ? null : ConnKeyBox.Text.Trim(),
                    Enabled = true
                }).ToArray();

                await _settings.SaveAsync(s);

                ConnNameBox.Clear();
                ConnUrlBox.Clear();
                ConnKeyBox.Clear();

                await LoadModelsTabAsync();
                ModelsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ModelsStatusText.Text = $"Error adding connection: {ex.Message}";
            }
        }

        private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadModelsTabAsync();
                ModelsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ModelsStatusText.Text = $"Error refreshing models: {ex.Message}";
            }
        }
    }
}
