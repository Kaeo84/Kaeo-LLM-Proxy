using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using System.Windows.Controls;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.Settings;

public partial class SettingsWindow : DialogWindow
{
    private readonly ExtensionSettingsStore _settings;

    /// <summary>Raised after a connection is added/removed or models are refreshed, so the
    /// tool window can re-pull the model list.</summary>
    public event Action? ModelsChanged;

    internal SettingsWindow(ExtensionSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        _ = LoadModelsTabAsync();
    }

    public void OpenTab(string tabName)
    {
        switch (tabName)
        {
            case "General":
                MainTabControl.SelectedItem = TabGeneral;
                break;
            case "Models":
                MainTabControl.SelectedItem = TabModels;
                break;
            case "Agents":
                MainTabControl.SelectedItem = TabAgents;
                break;
            case "MCP":
                MainTabControl.SelectedItem = TabMcp;
                break;
            default:
                MainTabControl.SelectedItem = TabGeneral;
                break;
        }
    }

    /// <summary>Rebuilds the connections→models tree from the live /api/tags of each connection.</summary>
    private async System.Threading.Tasks.Task LoadModelsTabAsync()
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

    /// <summary>Adds a new named connection (URL + optional key) and persists it.</summary>
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
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
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

    /// <summary>Re-pulls /api/tags for every enabled connection and updates the tree.</summary>
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

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;
}
