using System.Collections.Concurrent;
using System.Text;
using Kaeo.LlmProxy.Modules;
using Kaeo.LlmProxy.Ssh.Core.Models;
using Renci.SshNet;
using Serilog;

namespace Kaeo.LlmProxy.Ssh.Core.Services;

/// <summary>
/// Manages maintained SSH sessions for the module: connections stay open across tool calls,
/// are reused by key, and are closed automatically once their idle timeout elapses. Thread
/// safe; tool invocations run on MCP session tasks while the idle sweep runs on a timer.
/// </summary>
internal sealed class SshConnectionManager : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private readonly SshRepository _repository;
    private readonly ConcurrentDictionary<string, ManagedConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private System.Threading.Timer? _idleSweepTimer;

    public SshConnectionManager(SshRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Raised after connections are opened, closed, or swept. May fire on background threads;
    /// subscribers must marshal to their own context.
    /// </summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>Whether the idle sweep is active (the module's runnable state).</summary>
    public bool IsRunning => _idleSweepTimer is not null;

    /// <summary>Whether at least one SSH connection is currently tracked.</summary>
    public bool HasOpenConnections => !_connections.IsEmpty;

    /// <summary>Starts the idle sweep. Safe to call when already running.</summary>
    public void Start()
    {
        if (_idleSweepTimer is not null)
            return;

        _idleSweepTimer = new System.Threading.Timer(SweepIdleConnections, null, SweepInterval, SweepInterval);
        Log.Information("SSH connection manager started (idle sweep every {Seconds}s)", SweepInterval.TotalSeconds);
    }

    /// <summary>Stops the idle sweep and closes every open connection. Safe when not running.</summary>
    public Task StopAsync()
    {
        System.Threading.Timer? timer = Interlocked.Exchange(ref _idleSweepTimer, null);
        timer?.Dispose();

        DisconnectAll();
        Log.Information("SSH connection manager stopped");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the connection described by <paramref name="request"/>, or returns the existing
    /// key when an open connection already exists for it. Records the MCP session and client
    /// address that opened the connection.
    /// </summary>
    public async Task<string> ConnectAsync(SshConnectionRequest request, McpSessionInfo? opener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Self-start the sweep so connections always idle out even when the host never called
        // StartAsync (e.g. the module was enabled after startup).
        Start();

        // Fast path: an open connection for this key is reused as-is.
        if (_connections.TryGetValue(request.Key, out ManagedConnection? existing) && existing.Client.IsConnected)
        {
            existing.Touch();
            return request.Key;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: a concurrent caller may have connected meanwhile.
            if (_connections.TryGetValue(request.Key, out existing) && existing.Client.IsConnected)
            {
                existing.Touch();
                return request.Key;
            }

            // Drop a stale entry (disconnected transport) before reconnecting.
            if (existing is not null && _connections.TryRemove(request.Key, out ManagedConnection? stale))
                stale.Dispose();

            ManagedConnection connection = await OpenConnectionAsync(request, opener, cancellationToken);
            _connections[request.Key] = connection;

            Log.Information("SSH connection {Key} opened to {Host}:{Port} as {Username}",
                request.Key, request.Host, request.Port, request.Username);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);

            return request.Key;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Executes <paramref name="commandText"/> on the open connection identified by
    /// <paramref name="connectionKey"/>. Returns null when no such connection is open.
    /// </summary>
    public async Task<SshCommandResult?> ExecuteAsync(
        string connectionKey, string commandText, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        if (!_connections.TryGetValue(connectionKey, out ManagedConnection? connection))
            return null;

        if (!connection.Client.IsConnected)
        {
            // The transport died (network drop, remote restart): clean up and report.
            if (_connections.TryRemove(connectionKey, out ManagedConnection? dead))
            {
                dead.Dispose();
                ConnectionsChanged?.Invoke(this, EventArgs.Empty);
            }

            return null;
        }

        using SshCommand command = connection.Client.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(commandTimeoutSeconds, 1));

        await command.ExecuteAsync(cancellationToken);

        connection.Touch();

        return new SshCommandResult
        {
            ExitCode = command.ExitStatus ?? -1,
            Output = command.Result ?? string.Empty,
            Error = command.Error ?? string.Empty,
        };
    }

    /// <summary>
    /// Closes the connection identified by <paramref name="connectionKey"/> (a stored
    /// connection name or an ad-hoc <c>user@host:port</c> key). Returns false when no such
    /// connection was tracked.
    /// </summary>
    public bool Disconnect(string connectionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        if (!_connections.TryRemove(connectionKey, out ManagedConnection? connection))
            return false;

        connection.Dispose();
        Log.Information("SSH connection {Key} closed", connectionKey);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Closes every open connection.</summary>
    public void DisconnectAll()
    {
        foreach (string key in _connections.Keys.ToList())
            Disconnect(key);
    }

    /// <summary>Whether a connection with <paramref name="connectionKey"/> is currently open.</summary>
    public bool IsOpen(string connectionKey) =>
        _connections.TryGetValue(connectionKey, out ManagedConnection? connection) && connection.Client.IsConnected;

    /// <summary>Snapshot of all tracked connections for the configuration UI and ssh_list tool.</summary>
    public IReadOnlyList<OpenSshConnectionInfo> GetSnapshot()
    {
        List<OpenSshConnectionInfo> snapshot = [];

        foreach (ManagedConnection connection in _connections.Values)
        {
            snapshot.Add(new OpenSshConnectionInfo
            {
                Key = connection.Key,
                Host = connection.Host,
                Port = connection.Port,
                Username = connection.Username,
                McpSessionId = connection.McpSessionId,
                OpenedByClientAddress = connection.OpenedByClientAddress,
                OpenedUtc = connection.OpenedUtc,
                LastActivityUtc = connection.LastActivityUtc,
                IdleTimeoutSeconds = connection.IdleTimeoutSeconds,
                IsConnected = connection.Client.IsConnected,
            });
        }

        return snapshot
            .OrderBy(info => info.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        System.Threading.Timer? timer = Interlocked.Exchange(ref _idleSweepTimer, null);
        timer?.Dispose();

        foreach (ManagedConnection connection in _connections.Values)
            connection.Dispose();

        _connections.Clear();
        _connectLock.Dispose();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static async Task<ManagedConnection> OpenConnectionAsync(
        SshConnectionRequest request, McpSessionInfo? opener, CancellationToken cancellationToken)
    {
        List<AuthenticationMethod> authMethods = BuildAuthenticationMethods(request);

        ConnectionInfo connectionInfo = new(request.Host, request.Port, request.Username, [.. authMethods])
        {
            Timeout = ConnectTimeout,
            RetryAttempts = 1,
        };

        SshClient client = new(connectionInfo);
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new ManagedConnection
        {
            Key = request.Key,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            Client = client,
            McpSessionId = opener?.SessionId,
            OpenedByClientAddress = opener?.ClientAddress,
            IdleTimeoutSeconds = request.IdleTimeoutSeconds,
        };
    }

    /// <summary>
    /// Builds the authentication methods for a request: private key (with optional certificate
    /// and passphrase) takes priority; a password is offered as a fallback method so the server
    /// can pick whichever it accepts.
    /// </summary>
    private static List<AuthenticationMethod> BuildAuthenticationMethods(SshConnectionRequest request)
    {
        List<AuthenticationMethod> methods = [];

        if (!string.IsNullOrWhiteSpace(request.PrivateKey))
        {
            using MemoryStream keyStream = new(Encoding.UTF8.GetBytes(request.PrivateKey));

            PrivateKeyFile keyFile;
            if (!string.IsNullOrWhiteSpace(request.Certificate))
            {
                using MemoryStream certificateStream = new(Encoding.UTF8.GetBytes(request.Certificate));
                keyFile = new PrivateKeyFile(keyStream, request.Password ?? string.Empty, certificateStream);
            }
            else if (!string.IsNullOrWhiteSpace(request.Password))
            {
                keyFile = new PrivateKeyFile(keyStream, request.Password);
            }
            else
            {
                keyFile = new PrivateKeyFile(keyStream);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(request.Username, keyFile));
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
            methods.Add(new PasswordAuthenticationMethod(request.Username, request.Password));

        if (methods.Count == 0)
            throw new InvalidOperationException("No authentication material available: provide a password or a private key.");

        return methods;
    }

    /// <summary>Timer callback: closes connections whose idle timeout has elapsed or whose transport died.</summary>
    private void SweepIdleConnections(object? state)
    {
        try
        {
            SshSettings settings = _repository.LoadSettings();
            DateTime nowUtc = DateTime.UtcNow;
            bool changed = false;

            foreach (KeyValuePair<string, ManagedConnection> entry in _connections)
            {
                ManagedConnection connection = entry.Value;

                if (!connection.Client.IsConnected)
                {
                    if (_connections.TryRemove(entry.Key, out ManagedConnection? dead))
                    {
                        dead.Dispose();
                        changed = true;
                        Log.Information("SSH connection {Key} was lost and has been removed", entry.Key);
                    }

                    continue;
                }

                int effectiveTimeout = connection.IdleTimeoutSeconds > 0
                    ? connection.IdleTimeoutSeconds
                    : settings.DefaultIdleTimeoutSeconds;

                if (effectiveTimeout <= 0)
                    continue;

                if (nowUtc - connection.LastActivityUtc > TimeSpan.FromSeconds(effectiveTimeout))
                {
                    if (_connections.TryRemove(entry.Key, out ManagedConnection? idle))
                    {
                        idle.Dispose();
                        changed = true;
                        Log.Information("SSH connection {Key} closed after {Seconds}s idle", entry.Key, effectiveTimeout);
                    }
                }
            }

            if (changed)
                ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSH idle sweep failed");
        }
    }

    /// <summary>One tracked SSH session with its bookkeeping metadata.</summary>
    private sealed class ManagedConnection : IDisposable
    {
        private readonly object _activityLock = new();
        private DateTime _lastActivityUtc = DateTime.UtcNow;

        public required string Key { get; init; }

        public required string Host { get; init; }

        public required int Port { get; init; }

        public required string Username { get; init; }

        public required SshClient Client { get; init; }

        public string? McpSessionId { get; init; }

        public string? OpenedByClientAddress { get; init; }

        public DateTime OpenedUtc { get; init; } = DateTime.UtcNow;

        public DateTime LastActivityUtc
        {
            get { lock (_activityLock) return _lastActivityUtc; }
        }

        /// <summary>Idle timeout override in seconds; 0 uses the module-wide default.</summary>
        public int IdleTimeoutSeconds { get; init; }

        public void Touch()
        {
            lock (_activityLock)
                _lastActivityUtc = DateTime.UtcNow;
        }

        public void Dispose()
        {
            try
            {
                Client.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error disposing SSH connection {Key}", Key);
            }
        }
    }
}
