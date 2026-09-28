using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Serilog;
using Serilog.Events;

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

        // Seed a crash-only logger before anything else can fail. Serilog is not configured until
        // TrayApplicationContext runs, so an exception in startup would otherwise have no sink at all.
        InitializeCrashLogger();

        // Surface ALL unhandled exceptions instead of silently swallowing them.
        //
        // These handlers are registered in every build, including Debug. Registering them only for
        // release meant a Debug session had no handler AND ran under UnhandledExceptionMode.
        // ThrowException, so an exception on the UI thread — most often in one of the many async void
        // event handlers — killed the process outright with nothing recorded anywhere. The debugger
        // breaks first when attached, which preserves the old debugging behavior, so there is nothing
        // lost by handling them here too.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandledException("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportUnhandledException("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportUnhandledException("Unobserved Task", e.Exception);
            e.SetObserved();
        };

        AppSettings settings = AppSettings.Load();

        // Program owns the single shared AppDatabase for the whole process. It is passed into
        // TrayApplicationContext rather than created there so a second connection to the same
        // file can never exist.
        using AppDatabase database = new(settings.Logging);
        settings.ApplyRuntimeSettings(database.LoadRuntimeSettings());

        // Binding the proxy to anything other than "localhost" (e.g. 0.0.0.0 / a specific NIC IP)
        // requires the process to be elevated, because http.sys only grants non-elevated processes
        // free use of the loopback binding. When the user opts in via the "Run as administrator"
        // setting, re-launch ourselves elevated via a UAC prompt; if the prompt is declined we
        // simply continue non-elevated (localhost still works). This runs in debug builds too so
        // the option works when testing 0.0.0.0 from another machine during development.
        if (settings.RunAsAdministrator && !IsRunningAsAdministrator() && TryRelaunchElevated())
            return;

#if DEBUG
        // Debug builds that stay non-elevated cannot bind to a non-loopback address (http.sys
        // denies them to unprivileged processes). Force the loopback address so the proxy can
        // start without administrator rights. The configured address is honoured only when the
        // user opted into "Run as administrator" AND the process is actually elevated (via the
        // re-launch above), so 0.0.0.0 can be tested from another machine.
        if (!settings.RunAsAdministrator || !IsRunningAsAdministrator())
            settings.ListenAddress = "localhost";
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
                if (value is not null && SecretProtector.IsEncrypted(value)
                    && !SecretProtector.TryDecrypt(value, passphrase, out _))
                {
                    return false;
                }
            }
        }

        // Second pass: apply decryption.
        foreach (StoredCredential credential in settings.Credentials)
        {
            if (credential.Secret is not null && SecretProtector.IsEncrypted(credential.Secret))
                credential.Secret = SecretProtector.Decrypt(credential.Secret, passphrase);

            if (credential.PrivateKey is not null && SecretProtector.IsEncrypted(credential.PrivateKey))
                credential.PrivateKey = SecretProtector.Decrypt(credential.PrivateKey, passphrase);

            if (credential.Certificate is not null && SecretProtector.IsEncrypted(credential.Certificate))
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

    /// <summary>
    /// Records an unhandled exception to every channel available, then tells the user.
    /// </summary>
    /// <remarks>
    /// The order matters and is deliberate. Serilog handles the normal case; the direct file append is
    /// there because the Serilog pipeline itself may be the thing that failed, or may not be
    /// initialized yet, and losing the only record of a crash to a logging failure is the worst
    /// outcome available. Nothing here is allowed to throw: a handler that throws while handling a
    /// crash turns a diagnosable failure into a silent one.
    /// </remarks>
    private static void ReportUnhandledException(string source, Exception? ex)
    {
        if (ex is null)
            return;

        // Break at the throw site when a debugger is attached, so debugging behavior is unchanged by
        // registering these handlers in Debug builds.
        if (Debugger.IsAttached)
            Debugger.Break();

        Debug.WriteLine($"[UNHANDLED:{source}] {ex}");

        try
        {
            Log.Fatal(ex, "Unhandled exception ({Source})", source);
            Log.CloseAndFlush();
        }
        catch
        {
            // The logging pipeline is unavailable; the file append below is the remaining channel.
        }

        AppendCrashReport(source, ex);

        try
        {
            MessageBox.Show(
                $"An unhandled exception occurred ({source}):\n\n{ex.GetType().FullName}: {ex.Message}\n\n" +
                $"A report was written to:\n{CrashLogPath}\n\nex.StackTrace:\n{ex.StackTrace}",
                "Unhandled Exception",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Last-resort: never let the handler itself crash the process.
        }
    }

    /// <summary>Path of the direct crash log, alongside the application's other logs.</summary>
    private static string CrashLogPath =>
        Path.Combine(_crashLogDirectory, "unhandled-exceptions.log");

    private static string _crashLogDirectory = AppContext.BaseDirectory;

    /// <summary>
    /// Creates a minimal file-only Serilog logger so failures before the real configuration is loaded
    /// still reach disk.
    /// </summary>
    /// <remarks>
    /// Reads the log directory from settings when it can, falling back to the application directory.
    /// Any failure is ignored: this is a best-effort safety net and must never prevent startup.
    /// </remarks>
    private static void InitializeCrashLogger()
    {
        try
        {
            AppSettings bootstrap = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(bootstrap.Logging.LogDirectory))
                _crashLogDirectory = bootstrap.Logging.LogDirectory;
        }
        catch
        {
            // Settings unreadable; the application directory is the fallback.
        }

        try
        {
            Directory.CreateDirectory(_crashLogDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(_crashLogDirectory, "startup-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            // No logger; ReportUnhandledException still writes the report file directly.
        }
    }

    /// <summary>
    /// Appends a crash report straight to a file, independent of the logging pipeline.
    /// </summary>
    /// <remarks>
    /// A plain text append that uses no configuration and no third-party code path, because the point
    /// is to survive whatever just went wrong. This is the channel that works even when the Serilog
    /// pipeline is the thing that failed, and even when the process is being torn down.
    /// </remarks>
    private static void AppendCrashReport(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(_crashLogDirectory);

            string report =
                $"{new string('=', 80)}{Environment.NewLine}" +
                $"Timestamp : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}" +
                $"Source    : {source}{Environment.NewLine}" +
                $"Type      : {ex.GetType().FullName}{Environment.NewLine}" +
                $"Message   : {ex.Message}{Environment.NewLine}" +
                $"Stack     :{Environment.NewLine}{ex}{Environment.NewLine}";

            File.AppendAllText(CrashLogPath, report);
        }
        catch
        {
            // Nothing left to try; the user-facing dialog still reports the exception.
        }
    }
}