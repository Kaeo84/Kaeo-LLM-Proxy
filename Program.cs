using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;

namespace Kaeo.LlmProxy;

internal static class Program
{
    private const string MutexName = "Global\\Kaeo.LlmProxy.SingleInstance";
    private static readonly string _appIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        // Surface ALL unhandled exceptions instead of silently swallowing them.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnhandledException("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowUnhandledException("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ShowUnhandledException("Unobserved Task", e.Exception);
            e.SetObserved();
        };

        AppSettings settings = AppSettings.Load();
        AppDatabase database = new(settings.Logging);
        settings.ApplyRuntimeSettings(database.LoadRuntimeSettings());

        if (!settings.AllowMultipleInstances)
        {
            Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Kaeo LLM Proxy is already running.\n\n" +
                    "Only one instance is allowed at a time. Check the system tray for the existing instance.\n\n" +
                    "To run multiple instances simultaneously, set \"AllowMultipleInstances\": true in settings.jsonc.",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Keep the mutex alive for the lifetime of the process.
            GC.KeepAlive(mutex);
        }

        Application.Run(new TrayApplicationContext(settings, database));
    }

    internal static Icon GetApplicationIcon()
    {
        if (!File.Exists(_appIconPath))
            return SystemIcons.Application;

        try
        {
            return new Icon(_appIconPath);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static void ShowUnhandledException(string source, Exception? ex)
    {
        if (ex is null)
            return;

        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debugger.Break();

        System.Diagnostics.Debug.WriteLine($"[UNHANDLED:{source}] {ex}");

        try
        {
            MessageBox.Show(
                $"An unhandled exception occurred ({source}):\n\n{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}",
                "Unhandled Exception",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Last-resort: never let the handler itself crash the process.
        }
    }
}