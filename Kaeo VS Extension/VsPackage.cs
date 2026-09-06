using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
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

    /// <summary>Command set GUID. Must match <c>guidKaeoCmdSet</c> in Commands.vsct.</summary>
    private static readonly Guid CommandSet = new("a1b2c3d4-0000-4a6d-9c2f-000000000002");

    /// <summary>View &gt; Kaeo Assistant command ID. Must match <c>cmdidOpenKaeoAssistant</c> in Commands.vsct.</summary>
    private const int OpenKaeoAssistantCommandId = 0x0100;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // The tool window is also reachable via View > Other Windows (from [ProvideToolWindow]).
        // Register the explicit View-menu button from the compiled Commands.vsct command table.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
        {
            var commandId = new CommandID(CommandSet, OpenKaeoAssistantCommandId);
            commandService.AddCommand(new OleMenuCommand(OnOpenKaeoAssistant, commandId));
        }
    }

    /// <summary>Finds (creating if needed) and shows the Kaeo Assistant tool window.</summary>
    private void OnOpenKaeoAssistant(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ToolWindowPane? window = FindToolWindow(typeof(ToolWindowPaneImpl), 0, true);
        if (window?.Frame is IVsWindowFrame frame)
        {
            ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }
}
