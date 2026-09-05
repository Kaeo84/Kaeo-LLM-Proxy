using System.Windows;

namespace Kaeo.LlmProxy.VSExtension.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
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

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();
}
