using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
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

        // Binding the proxy to anything other than "localhost" (e.g. 0.0.0.0 / a specific NIC IP)
        // requires the process to be elevated, because http.sys only grants non-elevated processes
        // free use of the loopback binding. Re-launch ourselves elevated via a UAC prompt so the
        // proxy can listen on all interfaces without the user having to right-click "Run as admin".
        // If the user declines the UAC prompt we simply continue non-elevated (localhost still works).
        if (!IsRunningAsAdministrator() && TryRelaunchElevated())
            return;

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

        // Load mappings early so we can resolve the passphrase and decrypt API keys
        // before the TrayApplicationContext starts the proxy.
        settings.ModelMappings = [.. database.LoadModelMappings()];
        ResolvePassphrase(settings);

        Application.Run(new TrayApplicationContext(settings, database));
    }

    /// <summary>
    /// Returns true when the current process is running with an elevated (Administrator) token.
    /// </summary>
    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Attempts to re-launch the current executable elevated via a UAC prompt. Returns true when
    /// an elevated instance was started (the caller should exit the current non-elevated process).
    /// Returns false if the user declined the prompt or elevation could not be started, in which
    /// case the caller should continue running non-elevated.
    /// </summary>
    private static bool TryRelaunchElevated()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            };

            using var elevated = System.Diagnostics.Process.Start(startInfo);
            return elevated is not null;
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt (ERROR_CANCELLED). Continue non-elevated.
            return false;
        }
    }

    /// <summary>
    /// Resolves the passphrase needed to decrypt encrypted API keys in model mappings.
    /// Tries the stored <see cref="AppSettings.SecurityPassphrase"/> first; if absent or
    /// incorrect, prompts the user with an optional "remember" checkbox.
    /// </summary>
    private static void ResolvePassphrase(AppSettings settings)
    {
        bool hasEncrypted = settings.ModelMappings.Any(m => SecretProtector.IsEncrypted(m.ApiKey));

        if (!hasEncrypted)
        {
            // No encrypted keys yet; carry the stored passphrase forward for future saves.
            settings.RuntimePassphrase = settings.SecurityPassphrase;
            return;
        }

        // Try the stored passphrase first.
        if (!string.IsNullOrEmpty(settings.SecurityPassphrase))
        {
            if (TryDecryptAllMappings(settings.ModelMappings, settings.SecurityPassphrase))
            {
                settings.RuntimePassphrase = settings.SecurityPassphrase;
                return;
            }

            // Stored passphrase is wrong; remove it so it is not reused on next launch.
            settings.SecurityPassphrase = null;
            settings.Save();
        }

        // Prompt until the user supplies a valid passphrase or cancels.
        while (true)
        {
            if (!PassphraseDialog.Prompt(
                    owner: null,
                    "One or more model mappings have encrypted API keys.\nEnter the passphrase to decrypt them.",
                    out string passphrase,
                    out bool remember))
            {
                // User cancelled — encrypted keys stay encrypted; upstream auth will fail for those mappings.
                return;
            }

            if (TryDecryptAllMappings(settings.ModelMappings, passphrase))
            {
                settings.RuntimePassphrase = passphrase;

                if (remember)
                {
                    settings.SecurityPassphrase = passphrase;
                    settings.Save();
                }

                return;
            }

            MessageBox.Show(
                "The passphrase could not decrypt the stored API keys. Please try again.",
                "Invalid Passphrase",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Verifies that every encrypted API key can be decrypted with <paramref name="passphrase"/>,
    /// then applies the decryption in-place. Returns false if any key fails authentication.
    /// </summary>
    private static bool TryDecryptAllMappings(List<ModelMapping> mappings, string passphrase)
    {
        // First pass: verify all encrypted keys can be decrypted (all-or-nothing).
        foreach (ModelMapping mapping in mappings)
        {
            if (SecretProtector.IsEncrypted(mapping.ApiKey)
                && !SecretProtector.TryDecrypt(mapping.ApiKey!, passphrase, out _))
            {
                return false;
            }
        }

        // Second pass: apply decryption.
        foreach (ModelMapping mapping in mappings)
        {
            if (SecretProtector.IsEncrypted(mapping.ApiKey))
                mapping.ApiKey = SecretProtector.Decrypt(mapping.ApiKey!, passphrase);
        }

        return true;
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