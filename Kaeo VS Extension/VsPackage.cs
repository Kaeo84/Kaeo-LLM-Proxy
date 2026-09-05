using System;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using Kaeo.LlmProxy.VSExtension.ToolWindow;

namespace Kaeo.LlmProxy.VSExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideToolWindow(typeof(ToolWindowPaneImpl))]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(PackageGuidString)]
public sealed class VsPackage : AsyncPackage
{
    public const string PackageGuidString = "d1f6a6c2-0000-4a6d-9c2f-000000000000";
    public const string CmdSetGuidString = "a1b2c3d4-0000-4a6d-9c2f-000000000002";

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await this.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
        {
            var cmdId = new CommandID(new Guid(CmdSetGuidString), 0x0100);
            var menuItem = new MenuCommand(ShowToolWindow, cmdId);
            commandService.AddCommand(menuItem);
        }
    }

    private void ShowToolWindow(object? sender, EventArgs e)
    {
        _ = this.JoinableTaskFactory.RunAsync(async () =>
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await this.ShowToolWindowAsync(typeof(ToolWindowPaneImpl), 0, true, this.DisposalToken);
        });
    }
}
