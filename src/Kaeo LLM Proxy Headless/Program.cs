using System.Diagnostics;
using System.Runtime.InteropServices;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Serilog;

namespace Kaeo.LlmProxy.Headless;

/// <summary>
/// Cross-platform console host for the proxy engine. Runs the same <see cref="ProxyHostController"/>
/// as the Windows tray app but with no WinForms: it boots settings/database/credentials/modules,
/// starts the proxy + MCP server, and waits for Ctrl+C (or SIGTERM on Linux/macOS) to drain and exit.
/// This is the entry point used on Linux and macOS (and for running the engine as a service on Windows).
/// </summary>
internal static class Program
{
    // A distinct name from the tray host's Global\ mutex so a headless instance and a tray instance
    // can coexist; two headless instances still exclude each other unless AllowMultipleInstances is set.
    private const string MutexName = "Kaeo.LlmProxy.SingleInstance.Headless";

    private static Mutex? _singleInstanceMutex;
    private static string _crashLogDirectory = AppContext.BaseDirectory;

    private static async Task<int> Main(string[] args)
    {
        // Offline packaging verbs run before any host startup (no settings, DB, or mutex).
        if (args.Length > 0 && args[0].StartsWith("--module-", StringComparison.OrdinalIgnoreCase))
            return ModuleCli.Run(args);

        // Seed a crash-only logger before anything else can fail; reuse the bootstrap settings so
        // AppSettings.Load only runs once.
        AppSettings settings = InitializeCrashLogger() ?? AppSettings.Load();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportUnhandledException("AppDomain", e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportUnhandledException("Unobserved Task", e.Exception, fatal: false);
            e.SetObserved();
        };

        #if DEBUG
        // A non-elevated process cannot bind a non-loopback address on Windows (http.sys); force
        // loopback in debug so the proxy starts without administrator rights.
        settings.ListenAddress = "localhost";
#endif

        try
        {
            // One shared AppDatabase for the whole process; the controller borrows it and must not
            // dispose it (the using here owns it). Opened before the single-instance check because
            // the AllowMultipleInstances preference lives in runtime settings; the database
            // tolerates concurrent instances (sharing-violation retries).
            using AppDatabase database = new(settings.Logging);
            settings.ApplyRuntimeSettings(database.LoadRuntimeSettings());

            // Single-instance lock (opt-out via "Allow multiple instances" in Settings; multiple
            // instances are allowed by default).
            if (!settings.AllowMultipleInstances && !TryAcquireSingleInstanceMutex())
                return 1;

            settings.ModelMappings = [.. database.LoadModelMappings()];
            settings.Credentials = [.. database.LoadCredentials()];
            ResolvePassphrase(settings);

            ModuleHost moduleHost = new(database, settings);
            moduleHost.LoadRegisteredModules();

            await using ProxyHostController controller = new(settings, database, moduleHost);
            controller.StatusChanged += (_, status) => Console.WriteLine($"[proxy] {status}");
            controller.ProxyStartFailed += (_, ex) => Console.Error.WriteLine($"[proxy] start failed: {ex.Message}");

            await controller.StartAsync();

            await using WebDashboardHost dashboard = new(settings, controller);
            try
            {
                await dashboard.StartAsync();
                if (dashboard.Url is not null)
                    Console.WriteLine($"[dashboard] {dashboard.Url}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Web dashboard failed to start; continuing without it");
            }

            Console.WriteLine($"Kaeo LLM Proxy (headless) running on {settings.ListenAddress}:{settings.ListenPort}. Press Ctrl+C to stop.");
            await WaitForShutdownAsync();

            Console.WriteLine("Kaeo LLM Proxy (headless) shutting down…");
            await dashboard.StopAsync();
            await controller.StopAsync();
            return 0;
        }
        catch (Exception ex)
        {
            ReportUnhandledException("Main", ex, fatal: true);
            return 1;
        }
        finally
        {
            ReleaseSingleInstanceMutex();
        }
    }

    /// <summary>
    /// Blocks until Ctrl+C (all platforms) or SIGTERM/SIGINT (Linux/macOS) requests shutdown.
    /// </summary>
    private static async Task WaitForShutdownAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            e.Cancel = true; // keep the process alive so we can drain gracefully
            tcs.TrySetResult();
        };
        Console.CancelKeyPress += onCtrlC;

        // SIGTERM is how service managers (systemd, launchd, docker stop) ask a process to exit.
        using PosixSignalRegistration? sigterm = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => tcs.TrySetResult());
        using PosixSignalRegistration? sigint = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => tcs.TrySetResult());

        await tcs.Task;
        Console.CancelKeyPress -= onCtrlC;
    }

    private static bool TryAcquireSingleInstanceMutex()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
            return true;

        ReleaseSingleInstanceMutex();
        Console.Error.WriteLine(
            "Kaeo LLM Proxy (headless) is already running and single-instance mode is enabled. " +
            "Enable \"Allow multiple instances\" in the Settings tab (Windows app or web dashboard) to run multiple instances.");
        return false;
    }

    private static void ReleaseSingleInstanceMutex()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (acquisition failed); nothing to release.
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    /// <summary>
    /// Resolves the passphrase needed to decrypt encrypted credential secrets without any UI.
    /// Tries the <c>KAEO_SECURITY_PASSPHRASE</c> environment variable first, then the stored
    /// hash / legacy plaintext. If encrypted secrets exist but none of these decrypt them, the host
    /// logs a warning and continues with the secrets left encrypted (degraded, never a crash).
    /// </summary>
    private static void ResolvePassphrase(AppSettings settings)
    {
        bool hasEncrypted = settings.Credentials.Any(c => SecretProtector.HasAnyEncryptedSecret(c));
        bool isHashed = SecretProtector.IsHashedPassphrase(settings.SecurityPassphrase);
        bool isLegacy = !string.IsNullOrEmpty(settings.SecurityPassphrase) && !isHashed;

        if (!hasEncrypted && isHashed)
        {
            settings.RuntimePassphrase = SecretProtector.StripHashPrefix(settings.SecurityPassphrase!);
            return;
        }

        if (!hasEncrypted && isLegacy)
        {
            string raw = settings.SecurityPassphrase!;
            settings.SecurityPassphrase = SecretProtector.HashPassphrase(raw);
            settings.RuntimePassphrase = SecretProtector.StripHashPrefix(settings.SecurityPassphrase);
            TrySave(settings);
            return;
        }

        if (!hasEncrypted)
        {
            settings.RuntimePassphrase = settings.SecurityPassphrase;
            return;
        }

        string? env = Environment.GetEnvironmentVariable("KAEO_SECURITY_PASSPHRASE");
        string? candidate = !string.IsNullOrEmpty(env) ? env
            : isHashed ? SecretProtector.StripHashPrefix(settings.SecurityPassphrase!)
            : isLegacy ? settings.SecurityPassphrase
            : null;

        if (candidate is not null && TryDecryptAllSecrets(settings, candidate))
        {
            settings.RuntimePassphrase = candidate;
            // Migrate a legacy plaintext passphrase to the stored hash form, but never persist an
            // environment-supplied passphrase to disk.
            if (isLegacy && string.IsNullOrEmpty(env))
            {
                settings.SecurityPassphrase = SecretProtector.HashPassphrase(candidate);
                TrySave(settings);
            }
            return;
        }

        Log.Warning("Encrypted credentials are present but no valid passphrase is available non-interactively. " +
            "Set KAEO_SECURITY_PASSPHRASE or store a passphrase via the tray app. Continuing with secrets encrypted.");
        settings.RuntimePassphrase = null;
    }

    private static void TrySave(AppSettings settings)
    {
        try
        {
            settings.Save();
        }
        catch (IOException ex)
        {
            // Another instance may hold the settings file; degrade instead of crashing.
            Log.Warning(ex, "Could not persist settings");
        }
    }

    private static bool TryDecryptAllSecrets(AppSettings settings, string passphrase)
    {
        // First pass: verify all encrypted secrets can be decrypted (all-or-nothing).
        foreach (StoredCredential credential in settings.Credentials)
        {
            foreach (string? value in SecretProtector.EnumerateSecretMaterial(credential))
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

    private static string CrashLogPath => Path.Combine(_crashLogDirectory, "unhandled-exceptions.log");

    /// <summary>
    /// Creates a minimal file-only Serilog logger so failures before the real configuration is loaded
    /// still reach disk. Returns the bootstrap settings when they loaded so Main can reuse them.
    /// </summary>
    private static AppSettings? InitializeCrashLogger()
    {
        AppSettings? bootstrap = null;
        try
        {
            bootstrap = AppSettings.Load();
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

        return bootstrap;
    }

    /// <summary>
    /// Records an unhandled exception to every available channel (Serilog, a direct file append, and
    /// stderr). Nothing here may throw: a handler that throws while handling a crash turns a
    /// diagnosable failure into a silent one.
    /// </summary>
    private static void ReportUnhandledException(string source, Exception? ex, bool fatal)
    {
        if (ex is null)
            return;

        if (Debugger.IsAttached)
            Debugger.Break();

        Debug.WriteLine($"[UNHANDLED:{source}] {ex}");

        try
        {
            if (fatal)
            {
                Log.Fatal(ex, "Unhandled exception ({Source})", source);
                Log.CloseAndFlush();
            }
            else
            {
                Log.Error(ex, "Unhandled exception ({Source})", source);
            }
        }
        catch
        {
            // The logging pipeline is unavailable; the file append below is the remaining channel.
        }

        AppendCrashReport(source, ex);

        try
        {
            Console.Error.WriteLine($"Unhandled exception ({source}): {ex.GetType().FullName}: {ex.Message}");
        }
        catch
        {
            // Last-resort: never let the handler itself crash the process.
        }
    }

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
            // Nothing left to try; stderr still reports the exception.
        }
    }
}