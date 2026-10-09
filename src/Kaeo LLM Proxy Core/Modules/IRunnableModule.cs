namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Optional contract for modules that run a network service (listener/server). The host starts
/// runnable modules after the proxy starts and stops them on shutdown; modules may also start
/// and stop themselves on the fly from their configuration UI.
/// </summary>
public interface IRunnableModule
{
    /// <summary>Whether the module's service is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Raised when the module's display status changes (started, stopped, error...).</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>Starts the module's service. Must be safe to call when already running.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the module's service. Must be safe to call when not running.</summary>
    Task StopAsync();
}
