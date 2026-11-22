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

    /// <summary>L3: guards against double-hooking the process-wide exception handlers.</summary>
    private static bool _globalHandlersHooked;

    /// <summary>L3: release process-wide hooks and the static instance when the package is disposed.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            if (_globalHandlersHooked)
            {
                _globalHandlersHooked = false;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            }
        }
        base.Dispose(disposing);
    }

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

        // Seed the default instruction files once at package load (off the per-turn path) so the
        // common case has its rules on disk without every agent turn re-checking (review M2).
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var root = await InstructionFileLoader.GetSolutionRootPathAsync().ConfigureAwait(false);
                if (root is null)
                    return;
                var settings = await new ExtensionSettingsStore().LoadAsync().ConfigureAwait(false);
                IEnumerable<InstructionEntry> entries = settings.Instructions is { Length: > 0 }
                    ? settings.Instructions
                    : InstructionFileLoader.DefaultEntries();
                await Task.Run(() => InstructionFileLoader.EnsureDefaultFiles(entries, root)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"Failed to seed default instruction files: {ex.Message}");
            }
        });
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
        if (_globalHandlersHooked)
            return;
        _globalHandlersHooked = true;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => DebugLog.Error("Unhandled exception", e.ExceptionObject as Exception);

    /// <summary>
    /// Logs unobserved task exceptions, but filters out known-benign infrastructure faults that
    /// originate outside our code: VS's ServiceBroker/ServiceHub named-pipe teardown raises
    /// "Pipe is broken" IOExceptions through Nerdbank.Streams when a ServiceHub host recycles,
    /// and net48's StreamReader has a benign race (NullReferenceException in ReadBufferAsync)
    /// when a stream is disposed mid-read. These are unavoidable in the devenv process and
    /// would otherwise flood the debug window with ERROR noise. Anything else is logged, and
    /// benign faults are marked observed so the finalizer does not rethrow them.
    /// </summary>
    private static void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        if (IsBenignInfrastructureFault(e.Exception))
        {
            e.SetObserved();
            DebugLog.Verbose($"Suppressed benign unobserved task exception: {e.Exception.GetBaseException().GetType().Name}");
            return;
        }

        DebugLog.Error("Unobserved task exception", e.Exception);
    }

    /// <summary>True when every inner exception is a known-benign VS/net48 infrastructure fault.</summary>
    private static bool IsBenignInfrastructureFault(AggregateException ex)
    {
        foreach (var inner in ex.InnerExceptions)
        {
            if (!IsBenign(inner))
                return false;
        }
        return ex.InnerExceptions.Count > 0;
    }

    private static bool IsBenign(Exception ex) => ex switch
    {
        // VS ServiceBroker/ServiceHub named-pipe teardown (Nerdbank.Streams UsePipeWriter).
        System.IO.IOException io when io.Message.Contains("Pipe is broken") => true,
        // net48 StreamReader race on dispose-mid-read (the specific ReadBufferAsync frame).
        NullReferenceException nre when nre.StackTrace?.Contains("System.IO.StreamReader") == true
            && nre.StackTrace?.Contains("ReadBufferAsync") == true => true,
        _ => false,
    };

    /// <summary>Finds (creating if needed) and shows the Kaeo Assistant tool window.</summary>
    private void OnOpenKaeoAssistant(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ToolWindowPane? window = FindToolWindow(typeof(ToolWindowPaneImpl), 0, true);
        if (window?.Frame is IVsWindowFrame frame)
        {
            // L3: a failed Show must not throw out of a menu command handler; log and degrade.
            var hr = frame.Show();
            if (hr != VSConstants.S_OK)
                DebugLog.Warn($"Failed to show the Kaeo Assistant tool window (hr=0x{hr:X8}).");
        }
    }
}
