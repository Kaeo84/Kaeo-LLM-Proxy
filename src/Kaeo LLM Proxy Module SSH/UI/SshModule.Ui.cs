using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// Windows-only UI partial of <see cref="SshModule"/>. Implements <see cref="IModuleUi"/> with
/// WinForms pages; compiled only for the Windows TFM (the UI/ folder is excluded from the
/// portable net10.0 build so the module loads headless on Linux/macOS without this part).
/// </summary>
public sealed partial class SshModule : IModuleUi
{
    public object CreateConfigPage() => new SshConfigPage(this);

    /// <summary>Help page injected into the host Help tab.</summary>
    public object? CreateHelpPage()
    {
        System.Windows.Forms.TabPage page = new() { Text = "SSH Command", Padding = new System.Windows.Forms.Padding(8) };
        System.Windows.Forms.TextBox body = new()
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            BackColor = System.Drawing.SystemColors.Window,
            Text = HelpText,
        };
        page.Controls.Add(body);
        return page;
    }
}