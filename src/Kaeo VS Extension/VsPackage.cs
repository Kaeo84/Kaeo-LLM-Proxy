using System;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using Kaeo.LlmProxy.VSExtension.ToolWindow;
using Kaeo.LlmProxy.VSExtension.Diagnostics;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideToolWindow(typeof(ToolWindowPaneImpl))]
[ProvideToolWindow(typeof(DebugOutputPane))]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideBindingPath]
[Guid(PackageGuidString)]
public sealed class VsPackage : AsyncPackage
{
    public const string PackageGuidString = "d1f6a6c2-0000-4a6d-9c2f-000000000000";

    /// <summary>
    /// The loaded package instance, used by tool-window content that needs to show another window
    /// (<c>ShowToolWindowAsync</c> is an <see cref="AsyncPackage"/> method and is not reachable
    /// from a plain WPF control). Null until the package finishes initializing.
    /// </summary>
    public static VsPackage? Instance { get; private set; }

    /// <summary>Command set GUID. Must match <c>guidKaeoCmdSet</c> in Commands.vsct.</summary>
    private static readonly Guid CommandSet = new("a1b2c3d4-0000-4a6d-9c2f-000000000002");

    /// <summary>View &gt; Kaeo Assistant command ID. Must match <c>cmdidOpenKaeoAssistant</c> in Commands.vsct.</summary>
    private const int OpenKaeoAssistantCommandId = 0x0100;

    /// <summary>Extension install folder resolved once at package init so the resolve handler doesn't touch DTE.</summary>
    private static string? _extensionFolder;

    protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Safety net for the Core adapter assembly: the pkgdef codeBase entry normally
        // resolves it from the extension folder, but if that registration is missing
        // (partial deployment) fall back to loading from our install folder. BCL polyfills
        // and MEAI are NOT redirected here — devenv.exe.config already unifies them to the
        // SharedAssemblies/PrivateAssemblies copies, and our compile-time pins match those
        // assembly versions exactly.
        _extensionFolder = Path.GetDirectoryName(GetType().Assembly.Location);
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        Instance = this;
        HookGlobalExceptionLogging();
        DebugLog.Info($"Kaeo LLM Proxy package loaded — {BuildInfo.Timestamp}.");

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
        {
            var commandId = new CommandID(CommandSet, OpenKaeoAssistantCommandId);
            commandService.AddCommand(new OleMenuCommand(OnOpenKaeoAssistant, commandId));
        }
    }

    /// <summary>
    /// Fallback loader for the Core adapter assembly when the pkgdef codeBase entry is absent.
    /// </summary>
    private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
    {
        if (_extensionFolder is null)
            return null;

        var name = new AssemblyName(args.Name);
        if (name.Name != "Kaeo LLM Proxy VS Extension.Core")
            return null;

        var localPath = Path.Combine(_extensionFolder, "Kaeo LLM Proxy VS Extension.Core.dll");
        return File.Exists(localPath) ? Assembly.LoadFrom(localPath) : null;
    }

    /// <summary>
    /// Shows (creating if needed)
    /// <c>ShowToolWindowAsync</c> needs the package's protected <c>DisposalToken</c>.
    /// </summary>
    public Task ShowDebugWindowAsync()
        => ShowToolWindowAsync(typeof(DebugOutputPane), 0, create: true, DisposalToken);

    /// <summary>
    /// Forwards otherwise-invisible failures (crashes on background threads, dropped task
    /// exceptions) to the debug window. Hooked once; the handlers must never throw.
    /// </summary>
    private static void HookGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DebugLog.Error("Unhandled exception", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            DebugLog.Error("Unobserved task exception", e.Exception);
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
