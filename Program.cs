using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;

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
        #if DEBUG
                // In debug, rethrow so the debugger breaks at the exact throw line.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        #else
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (_, e) => ShowUnhandledException("UI thread", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    ShowUnhandledException("AppDomain", e.ExceptionObject as Exception);
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    ShowUnhandledException("Unobserved Task", e.Exception);
                    e.SetObserved();
                };
        #endif

        AppSettings settings = AppSettings.Load();

#if DEBUG
        // Debug builds never run elevated (the UAC re-launch is compiled out), and http.sys
        // denies non-loopback bindings to unprivileged processes. Force the loopback address so
        // the proxy can start without administrator rights during development.
        settings.ListenAddress = "localhost";
#endif

        // Program owns the single shared AppDatabase for the whole process. It is passed into
        // TrayApplicationContext rather than created there so a second connection to the same
        // file can never exist.
        using AppDatabase database = new(settings.Logging);
        settings.ApplyRuntimeSettings(database.LoadRuntimeSettings());

#if !DEBUG
        // Binding the proxy to anything other than "localhost" (e.g. 0.0.0.0 / a specific NIC IP)
        // requires the process to be elevated, because http.sys only grants non-elevated processes
        // free use of the loopback binding. When the user opts in via the "Run as administrator"
        // setting, re-launch ourselves elevated via a UAC prompt; if the prompt is declined we
        // simply continue non-elevated (localhost still works). Debug builds never force elevation
        // so development runs stay attachable.
        if (settings.RunAsAdministrator && !IsRunningAsAdministrator() && TryRelaunchElevated())
            return;
#endif

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

        // Load mappings and credentials early so we can resolve the passphrase and decrypt
        // their secrets before the TrayApplicationContext starts the proxy.
        settings.ModelMappings = [.. database.LoadModelMappings()];
        settings.Credentials = [.. database.LoadCredentials()];
        ResolvePassphrase(settings);

        // Load user-registered modules (browse-to-import registry). A module that fails to load
        // records its error in the registry and never blocks startup of the host or other modules.
        ModuleHost moduleHost = new(database, settings);
        moduleHost.LoadRegisteredModules();

        Application.Run(new TrayApplicationContext(settings, database, moduleHost));
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
                // ShellExecute defaults the elevated child's working directory to a system folder,
                // but this app resolves settings.jsonc and the Data folder relative to the CWD.
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
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
    /// Resolves the passphrase needed to decrypt encrypted credential secrets.
    /// Tries the stored <see cref="AppSettings.SecurityPassphrase"/> first; if absent or
    /// incorrect, prompts the user with an optional "remember" checkbox.
    /// </summary>
    private static void ResolvePassphrase(AppSettings settings)
    {
        bool hasEncrypted = settings.Credentials.Any(c =>
            SecretProtector.IsEncrypted(c.Secret)
            || SecretProtector.IsEncrypted(c.PrivateKey)
            || SecretProtector.IsEncrypted(c.Certificate));

        if (!hasEncrypted)
        {
            // No encrypted secrets yet; carry the stored passphrase forward for future saves.
            settings.RuntimePassphrase = settings.SecurityPassphrase;
            return;
        }

        // Try the stored passphrase first.
        if (!string.IsNullOrEmpty(settings.SecurityPassphrase))
        {
            if (TryDecryptAllSecrets(settings, settings.SecurityPassphrase))
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
                    "One or more stored secrets are encrypted.\nEnter the passphrase to decrypt them.",
                    out string passphrase,
                    out bool remember))
            {
                // User cancelled — encrypted secrets stay encrypted; auth that needs them will fail.
                return;
            }

            if (TryDecryptAllSecrets(settings, passphrase))
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
                "The passphrase could not decrypt the stored secrets. Please try again.",
                "Invalid Passphrase",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Verifies that every encrypted secret-material field (secret, private key, certificate)
    /// on every credential can be decrypted with <paramref name="passphrase"/>, then applies
    /// the decryption in-place. Returns false if any secret fails authentication.
    /// </summary>
    private static bool TryDecryptAllSecrets(AppSettings settings, string passphrase)
    {
        // First pass: verify all encrypted secrets can be decrypted (all-or-nothing).
        foreach (StoredCredential credential in settings.Credentials)
        {
            foreach (string? value in SecretMaterialValues(credential))
            {
                if (SecretProtector.IsEncrypted(value)
                    && !SecretProtector.TryDecrypt(value, passphrase, out _))
                {
                    return false;
                }
            }
        }

        // Second pass: apply decryption.
        foreach (StoredCredential credential in settings.Credentials)
        {
            if (SecretProtector.IsEncrypted(credential.Secret))
                credential.Secret = SecretProtector.Decrypt(credential.Secret, passphrase);

            if (SecretProtector.IsEncrypted(credential.PrivateKey))
                credential.PrivateKey = SecretProtector.Decrypt(credential.PrivateKey, passphrase);

            if (SecretProtector.IsEncrypted(credential.Certificate))
                credential.Certificate = SecretProtector.Decrypt(credential.Certificate, passphrase);
        }

        return true;
    }

    /// <summary>Enumerates the secret-bearing fields of a credential (may contain nulls/empties).</summary>
    private static IEnumerable<string?> SecretMaterialValues(StoredCredential credential)
    {
        yield return credential.Secret;
        yield return credential.PrivateKey;
        yield return credential.Certificate;
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