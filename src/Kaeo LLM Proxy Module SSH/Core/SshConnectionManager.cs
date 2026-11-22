using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Renci.SshNet.Common;

namespace Kaeo.LlmProxy.Module.Ssh;

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
    private readonly SshActivityLogger _activity;
    private readonly ConcurrentDictionary<string, ManagedConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    // M9: one lock per connection key so a slow/unreachable host cannot block connects to others.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectLocks = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _idleSweepTimer;
    // M12: lifecycle gates checked under the per-key lock so stop/dispose cannot race a connect.
    private volatile bool _stopped;
    private volatile bool _disposed;

    public SshConnectionManager(SshRepository repository, SshActivityLogger activity)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
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
        if (_disposed || _idleSweepTimer is not null)
            return;

        _stopped = false;
        _idleSweepTimer = new System.Threading.Timer(SweepIdleConnections, null, SweepInterval, SweepInterval);
        Log.Information("SSH connection manager started (idle sweep every {Seconds}s)", SweepInterval.TotalSeconds);
    }

    /// <summary>Stops the idle sweep and closes every open connection. Safe when not running.</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        System.Threading.Timer? timer = Interlocked.Exchange(ref _idleSweepTimer, null);
        if (timer is not null)
            await timer.DisposeAsync();

        // L13: offload blocking client teardown so the host's stop path is not held up.
        await Task.Run(() => DisconnectAll(force: true));
        Log.Information("SSH connection manager stopped");
    }

    /// <summary>
    /// Opens the connection described by <paramref name="request"/>, or returns the existing
    /// key when an open connection already exists for it. Records the MCP session and client
    /// address that opened the connection.
    /// </summary>
    public async Task<string> ConnectAsync(SshConnectionRequest request, McpSessionInfo? opener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // M12: refuse new work once the module is stopped/disposed so tool traffic cannot
        // resurrect the sweep or orphan a connection after teardown.
        if (_stopped || _disposed)
            throw new InvalidOperationException("The SSH module is stopped; connections cannot be opened.");

        // Self-start the sweep so connections always idle out even when the host never called
        // StartAsync (e.g. the module was enabled after startup).
        Start();

        string? openerSessionId = opener?.SessionId;

        // Fast path: an open connection for this key is reused as-is (same session only, H5).
        if (_connections.TryGetValue(request.Key, out ManagedConnection? existing) && existing.Client.IsConnected)
        {
            EnsureSessionOwns(existing, openerSessionId);
            existing.Touch();
            LogConnectReused(request.Key);
            return request.Key;
        }

        SemaphoreSlim connectLock = GetConnectLock(request.Key);
        await connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_stopped || _disposed)
                throw new InvalidOperationException("The SSH module is stopped; connections cannot be opened.");

            // Re-check under the lock: a concurrent caller may have connected meanwhile.
            if (_connections.TryGetValue(request.Key, out existing) && existing.Client.IsConnected)
            {
                EnsureSessionOwns(existing, openerSessionId);
                existing.Touch();
                LogConnectReused(request.Key);
                return request.Key;
            }

            // Drop a stale entry (disconnected transport) before reconnecting.
            if (existing is not null && _connections.TryRemove(request.Key, out ManagedConnection? stale))
                stale.Dispose();

            Stopwatch stopwatch = Stopwatch.StartNew();
            ManagedConnection connection;
            try
            {
                connection = await OpenConnectionAsync(request, opener, cancellationToken);
            }
            catch (Exception ex) when (ex is SshException or IOException or SocketException or OperationCanceledException or InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "connect")
                {
                    Target = request.Key,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    IsError = ex is not OperationCanceledException,
                    IsCancelled = ex is OperationCanceledException,
                    ErrorMessage = ex is OperationCanceledException
                        ? "Connection attempt timed out or was cancelled."
                        : $"Connection failed: {ex.Message}",
                });
                throw;
            }
            stopwatch.Stop();

            // M12: a stop/dispose may have landed while the handshake was in flight; do not
            // insert a live client after teardown (it would never be idled out).
            if (_stopped || _disposed)
            {
                connection.Dispose();
                throw new InvalidOperationException("The SSH module was stopped while connecting; the connection was closed.");
            }

            _connections[request.Key] = connection;

            Log.Information("SSH connection {Key} opened to {Host}:{Port} as {Username}",
                request.Key, request.Host, request.Port, request.Username);
            _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "connect")
            {
                Target = request.Key,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ResponseDetail = $"Connected to {request.Host}:{request.Port} as {request.Username}.",
            });
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);

            return request.Key;
        }
        finally
        {
            connectLock.Release();
        }
    }

    /// <summary>Per-key connect lock (M9); created atomically and never disposed while in use.</summary>
    private SemaphoreSlim GetConnectLock(string key) =>
        _connectLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// H5: a connection may only be used by the MCP session that opened it. A null on either
    /// side (e.g. UI-driven calls without a session) is treated as unrestricted for compatibility.
    /// </summary>
    private static void EnsureSessionOwns(ManagedConnection connection, string? requestingSessionId)
    {
        if (connection.McpSessionId is not null && requestingSessionId is not null &&
            !string.Equals(connection.McpSessionId, requestingSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SSH connection '{connection.Key}' belongs to a different MCP session and cannot be used from this one.");
        }
    }

    /// <summary>Records a reused connection at full level only; reuse carries no network activity.</summary>
    private void LogConnectReused(string connectionKey)
    {
        _activity.WriteAtFull(new McpActivityEntry(SshActivityLogger.Source, "connect")
        {
            Target = connectionKey,
            ResponseDetail = "Reused an already open connection.",
        });
    }

    /// <summary>
    /// Executes <paramref name="commandText"/> on the open connection identified by
    /// <paramref name="connectionKey"/>. Returns null when no such connection is open.
    /// </summary>
    public async Task<SshCommandResult?> ExecuteAsync(
        string connectionKey, string commandText, int commandTimeoutSeconds, CancellationToken cancellationToken,
        string? requestingSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        if (!_connections.TryGetValue(connectionKey, out ManagedConnection? connection))
            return null;

        // H5: reject execution on a connection opened by another MCP session.
        EnsureSessionOwns(connection, requestingSessionId);

        if (!connection.Client.IsConnected)
        {
            // The transport died (network drop, remote restart): clean up and report.
            if (_connections.TryRemove(connectionKey, out ManagedConnection? dead))
            {
                dead.Dispose();
                ConnectionsChanged?.Invoke(this, EventArgs.Empty);
                _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "close")
                {
                    Target = connectionKey,
                    IsError = true,
                    ErrorMessage = "Connection lost (transport died); removed.",
                });
            }

            return null;
        }

        // H1: mark the connection busy and touch it BEFORE executing so the idle sweep cannot
        // dispose the transport underneath a long-running command; clear busy in finally.
        connection.EnterBusy();
        try
        {
            connection.Touch();
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
        finally
        {
            connection.ExitBusy();
        }
    }

    /// <summary>
    /// Closes the connection identified by <paramref name="connectionKey"/> (a stored
    /// connection name or an ad-hoc <c>user@host:port</c> key). Returns false when no such
    /// connection was tracked.
    /// </summary>
    public bool Disconnect(string connectionKey, string? requestingSessionId = null, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        if (!_connections.TryGetValue(connectionKey, out ManagedConnection? connection))
            return false;

        // H5: only the opening session may close a connection (force bypasses for teardown).
        if (!force)
            EnsureSessionOwns(connection, requestingSessionId);

        // H1: defer closing a connection that is running a command; the sweep closes it once idle.
        if (!force && connection.IsBusy)
            return false;

        if (!_connections.TryRemove(connectionKey, out connection))
            return false;

        connection.Dispose();
        Log.Information("SSH connection {Key} closed", connectionKey);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Closes every open connection this session owns (or all when <paramref name="force"/>).
    /// Returns the number actually closed; busy connections are deferred unless forced.
    /// </summary>
    public int DisconnectAll(string? requestingSessionId = null, bool force = false)
    {
        int closed = 0;
        foreach (string key in _connections.Keys.ToList())
        {
            try
            {
                if (Disconnect(key, requestingSessionId, force))
                    closed++;
            }
            catch (InvalidOperationException)
            {
                // Owned by another session; leave it alone.
            }
        }

        return closed;
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
        if (_disposed)
            return;

        _disposed = true;
        _stopped = true;
        System.Threading.Timer? timer = Interlocked.Exchange(ref _idleSweepTimer, null);
        timer?.Dispose();

        DisconnectAll(force: true);
        _connections.Clear();
        // M12: do not dispose the per-key semaphores here — an in-flight connect may still hold
        // one between WaitAsync and Release. They carry no unmanaged handle; GC reclaims them.
        _connectLocks.Clear();

        // L13: notify subscribers so the UI clears stale rows after module removal.
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task<ManagedConnection> OpenConnectionAsync(
        SshConnectionRequest request, McpSessionInfo? opener, CancellationToken cancellationToken)
    {
        List<PrivateKeyFile> keyFiles = [];
        List<AuthenticationMethod> authMethods = BuildAuthenticationMethods(request, keyFiles);

        ConnectionInfo connectionInfo = new(request.Host, request.Port, request.Username, [.. authMethods])
        {
            Timeout = ConnectTimeout,
            RetryAttempts = 1,
        };

        SshClient client = new(connectionInfo);
        string hostId = $"{request.Host}:{request.Port}";
        // Verify the server's host key trust-on-first-use instead of accepting it blindly.
        client.HostKeyReceived += (_, e) =>
        {
            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.HostKey));
            e.CanTrust = VerifyHostKey(hostId, fingerprint);
        };
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch
        {
            // M8: release decrypted key material and auth-method handles on a failed handshake.
            DisposeAll(keyFiles);
            DisposeAll(authMethods);
            client.Dispose();
            throw;
        }

        // M8: the decrypted private-key bytes are not needed after authentication; dispose them
        // promptly to limit memory/pagefile exposure. Auth methods are tracked on the connection
        // and disposed when it closes (PrivateKeyAuthenticationMethod.Dispose does not free keys).
        DisposeAll(keyFiles);

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
            AuthMethods = authMethods,
        };
    }

    /// <summary>Disposes authentication material, swallowing and logging individual failures.</summary>
    private static void DisposeAll(IEnumerable<IDisposable> items)
    {
        foreach (IDisposable item in items)
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error disposing SSH authentication material");
            }
        }
    }

    /// <summary>
    /// Trust-on-first-use host-key check. The first key seen for a host:port is stored and
    /// trusted; a matching key is trusted; a DIFFERENT key is rejected (possible MITM). If the
    /// fingerprint store cannot be read the connection is rejected (fail closed) rather than
    /// accepting an unverifiable key.
    /// </summary>
    private bool VerifyHostKey(string hostId, string fingerprint)
    {
        try
        {
            string? stored = _repository.GetHostKeyFingerprint(hostId);
            if (stored is null)
            {
                _repository.SaveHostKeyFingerprint(hostId, fingerprint);
                Log.Information("SSH host key for {HostId} trusted on first use ({Fingerprint})", hostId, fingerprint);
                return true;
            }

            if (string.Equals(stored, fingerprint, StringComparison.OrdinalIgnoreCase))
                return true;

            Log.Warning("SSH host key for {HostId} CHANGED (stored {Stored}, presented {Presented}); rejecting connection (possible MITM).",
                hostId, stored, fingerprint);
            _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "host_key_rejected")
            {
                Target = hostId,
                IsError = true,
                ErrorMessage = "Host key changed since first use; connection rejected.",
            });
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
        {
            Log.Warning(ex, "SSH host key store unavailable for {HostId}; rejecting connection (fail closed).", hostId);
            return false;
        }
    }

    /// <summary>
    /// Builds the authentication methods for a request: private key (with optional certificate
    /// and passphrase) takes priority; a password is offered as a fallback method so the server
    /// can pick whichever it accepts. When no material was supplied at all, an empty-password
    /// method is offered so the handshake is still attempted and the server's real response
    /// surfaces (SSH.NET also probes "none" authentication on its own).
    /// </summary>
    private static List<AuthenticationMethod> BuildAuthenticationMethods(SshConnectionRequest request, List<PrivateKeyFile> keyFiles)
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

            keyFiles.Add(keyFile);
            methods.Add(new PrivateKeyAuthenticationMethod(request.Username, keyFile));
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
            methods.Add(new PasswordAuthenticationMethod(request.Username, request.Password));

        if (methods.Count == 0)
            methods.Add(new PasswordAuthenticationMethod(request.Username, string.Empty));

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

                // H1: never dispose a connection that is running a command; re-check next sweep.
                if (connection.IsBusy)
                    continue;

                if (!connection.Client.IsConnected)
                {
                    if (_connections.TryRemove(entry.Key, out ManagedConnection? dead))
                    {
                        dead.Dispose();
                        changed = true;
                        Log.Information("SSH connection {Key} was lost and has been removed", entry.Key);
                        _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "close")
                        {
                            Target = entry.Key,
                            IsError = true,
                            ErrorMessage = "Connection lost (detected by the idle sweep); removed.",
                        });
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
                        _activity.Write(new McpActivityEntry(SshActivityLogger.Source, "close")
                        {
                            Target = entry.Key,
                            ResponseDetail = $"Closed after {effectiveTimeout}s idle.",
                        });
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
        private int _busyCount;

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

        /// <summary>Authentication methods whose handles are released when the connection closes (M8).</summary>
        public IReadOnlyList<AuthenticationMethod> AuthMethods { get; init; } = [];

        /// <summary>True while at least one command is executing on this connection (H1).</summary>
        public bool IsBusy => Volatile.Read(ref _busyCount) > 0;

        public void EnterBusy() => Interlocked.Increment(ref _busyCount);

        public void ExitBusy() => Interlocked.Decrement(ref _busyCount);

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

            DisposeAll(AuthMethods);
        }
    }
}
