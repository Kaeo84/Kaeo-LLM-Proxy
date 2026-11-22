using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Manages all configured MCP servers: connects to them (HTTP Streamable or stdio),
/// pulls tool definitions via tools/list, caches them, and routes tool executions.
/// </summary>
internal sealed class McpServerManager
{
    private readonly ExtensionSettingsStore _settings;
    private readonly Dictionary<string, McpServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _heartbeatTimer;
    private TimeSpan _heartbeatInterval = TimeSpan.FromMinutes(5);

    /// <summary>The settings instance <see cref="InitializeAsync"/> loaded; pulls are cached back into it.</summary>
    private ExtensionSettings? _loadedSettings;

    /// <summary>Raised when a server's health status changes.</summary>
    public event Action<McpServer, bool>? ServerHealthChanged;

    public McpServerManager(ExtensionSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Loads all enabled MCP servers from settings and attempts to connect + pull tool definitions.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var all = await _settings.LoadAsync().ConfigureAwait(false);
        _loadedSettings = all;
        _heartbeatInterval = TimeSpan.FromMinutes(Math.Max(1, all.Defaults?.HeartbeatMinutes ?? 5));
        _servers.Clear();
        foreach (var server in all.McpServers ?? Array.Empty<McpServer>())
        {
            if (!server.Enabled || string.IsNullOrWhiteSpace(server.Name))
                continue;
            // The built-in VS tools are synthesized in GetAvailableToolDefinitions; never connect/pull.
            if (string.Equals(server.Transport, "builtin", StringComparison.OrdinalIgnoreCase))
                continue;
            _servers[server.Name!] = server;
            Observe(PullAndCacheAsync(server, ct));
        }

        // Start heartbeat monitoring
        StartHeartbeat();
    }

    /// <summary>
    /// Attaches a fault observer to a fire-and-forget task so a failure is logged instead of
    /// surfacing later as an UnobservedTaskException from the finalizer thread.
    /// </summary>
    private static void Observe(Task task)
    {
        // The continuation only logs; DebugLog never throws, so the continuation task
        // itself cannot fault and needs no further observation.
#pragma warning disable VSTHRD110
        task.ContinueWith(
            t => DebugLog.Error("Background MCP operation failed.", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
#pragma warning restore VSTHRD110
    }

    /// <summary>
    /// Starts periodic heartbeat monitoring for all enabled servers.
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatTimer = new Timer(
            _ => Observe(HeartbeatAsync()),
            null,
            _heartbeatInterval,
            _heartbeatInterval);
    }

    /// <summary>
    /// Stops the heartbeat timer.
    /// </summary>
    public void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Periodic health check for all enabled servers.
    /// </summary>
    private async Task HeartbeatAsync()
    {
        foreach (var server in _servers.Values.Where(s => s.Enabled))
        {
            try
            {
                await TestConnectionAsync(server).ConfigureAwait(false);
                bool wasStale = server.Stale;
                server.Stale = false;
                if (wasStale)
                    ServerHealthChanged?.Invoke(server, true);
            }
            catch
            {
                if (!server.Stale)
                {
                    server.Stale = true;
                    ServerHealthChanged?.Invoke(server, false);
                }
            }
        }
    }

    /// <summary>
    /// Returns all servers that are enabled but currently unhealthy.
    /// </summary>
    public IReadOnlyList<McpServer> GetUnhealthyServers()
    {
        return _servers.Values
            .Where(s => s.Enabled && s.Stale)
            .ToList();
    }

    /// <summary>
    /// Background pull used during initialization: never throws, so one dead server cannot fault
    /// an unobserved task. Cached definitions are kept and the server is marked stale.
    /// </summary>
    private async Task PullAndCacheAsync(McpServer server, CancellationToken ct)
    {
        try
        {
            await PullToolsAsync(server, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            server.Stale = true;
            DebugLog.Error($"MCP server '{server.Name}' could not be reached; keeping cached tools.", ex);
        }
    }

    /// <summary>
    /// Probes a server without changing any state. Performs the MCP <c>initialize</c> handshake so
    /// an endpoint that merely answers HTTP is not mistaken for a working server, and falls back to
    /// <c>tools/list</c> if the handshake is declined (some servers reject our protocol version but
    /// still serve tools). Returns a human-readable summary; throws when unreachable.
    /// </summary>
    public async Task<string> TestConnectionAsync(McpServer server, CancellationToken ct = default)
    {
        if (string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "The stdio transport is not implemented yet, so connectivity cannot be tested. Use an HTTP (streamable) endpoint.");

        if (string.IsNullOrWhiteSpace(server.Url))
            throw new InvalidOperationException("No URL is configured for this server.");

        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The URL must be an absolute http:// or https:// address.");

        // A dead endpoint would otherwise sit on HttpClient's 100s default and look like a hang.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;

        try
        {
            var (name, version) = await InitializeHttpAsync(server.Url!, server.ApiKey, token).ConfigureAwait(false);
            var summary = string.IsNullOrEmpty(version) ? name : $"{name} {version}";
            DebugLog.Info($"MCP test '{server.Name}': connected ({summary}).");
            return $"Connected to {summary}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DebugLog.Warn($"MCP test '{server.Name}': timed out after 15s.");
            throw new TimeoutException("The server did not respond within 15 seconds.");
        }
        catch (Exception ex)
        {
            // initialize declined or unsupported - confirm the endpoint still serves tools before failing.
            var tools = await PullToolsHttpAsync(server.Url!, server.ApiKey, token).ConfigureAwait(false);
            DebugLog.Info($"MCP test '{server.Name}': connected via tools/list ({tools.Count} tools).");
            return $"Connected - {tools.Count} tool(s) available (initialize handshake declined: {ex.GetBaseException().Message})";
        }
    }

    /// <summary>Sends an MCP <c>initialize</c> request and returns the advertised server name/version.</summary>
    private static async Task<(string Name, string Version)> InitializeHttpAsync(string url, string? apiKey, CancellationToken ct)
    {
        var session = new McpHttpSession(GetSharedHttpClient(url, apiKey));

                var doc = await SendJsonRpcAsync(session, CreateInitializeRequest(), ct).ConfigureAwait(false);
                var info = doc?["result"]?["serverInfo"];
                return (TextOf(info?["name"]), TextOf(info?["version"]));
    }

    /// <summary>Reads a JSON value as text without throwing when the server sends a non-string.</summary>
    private static string TextOf(JsonNode? node)
    {
        if (node is null)
            return string.Empty;
        try
        {
            return node.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return node.ToString();
        }
    }

    /// <summary>
    /// Pulls a server's tools without touching the settings file. Callers that own an
    /// <see cref="ExtensionSettings"/> instance (the settings window) use this and persist the
    /// merged result themselves, so two writers never race on the same file.
    /// </summary>
    public Task<IReadOnlyList<McpTool>> FetchToolsAsync(McpServer server, CancellationToken ct = default)
        => PullToolsAsync(server, persist: false, ct);

    /// <summary>
    /// Connects to the MCP server and pulls its tool definitions via tools/list, merging them with
    /// the cached list so a refresh never discards the user's per-tool enable choices.
    /// Throws when the server cannot be reached so an explicit refresh can report why.
    /// </summary>
    public async Task<IReadOnlyList<McpTool>> PullToolsAsync(McpServer server, bool persist = true, CancellationToken ct = default)
    {
        IReadOnlyList<McpTool> pulled;

        // HTTP Streamable transport: POST /mcp with JSON-RPC tools/list.
        if (server.Transport == "http" && !string.IsNullOrWhiteSpace(server.Url))
            pulled = await PullToolsHttpAsync(server.Url!, server.ApiKey, ct).ConfigureAwait(false);
        // stdio transport: spawn process and send JSON-RPC over stdin/stdout.
        else if (server.Transport == "stdio" && !string.IsNullOrWhiteSpace(server.Command))
            pulled = await PullToolsStdioAsync(server, ct).ConfigureAwait(false);
        else
            return server.Tools ?? Array.Empty<McpTool>();

        server.Tools = MergeTools(server.Tools, pulled).ToArray();
        server.Stale = false;
        server.LastSyncUtc = DateTime.UtcNow;

        if (persist)
            await PersistLoadedAsync().ConfigureAwait(false);

        return server.Tools;
    }

    /// <summary>
    /// Keeps each tool's enable flag across a refresh. Tools that disappeared drop out, new tools
    /// arrive enabled, and a tool the user switched off stays off even after the server is re-queried.
    /// </summary>
    internal static IReadOnlyList<McpTool> MergeTools(McpTool[]? previous, IReadOnlyList<McpTool> pulled)
    {
        var enabledByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in previous ?? Array.Empty<McpTool>())
            if (!string.IsNullOrWhiteSpace(tool.Name))
                enabledByName[tool.Name!] = tool.Enabled;

        foreach (var tool in pulled)
            if (!string.IsNullOrWhiteSpace(tool.Name))
                tool.Enabled = !enabledByName.TryGetValue(tool.Name!, out var enabled) || enabled;

        return pulled;
    }

    /// <summary>
    /// Persists the MCP-owned fields (tool cache, sync time, health) so they survive a restart.
    /// Merges into the CURRENT on-disk settings under the store's write lock: saving the whole
    /// snapshot loaded at init would clobber concurrent edits (connections, preferences,
    /// instructions) made by the settings window or other tools.
    /// </summary>
    private async Task PersistLoadedAsync()
    {
        if (_loadedSettings is null)
            return;

        var owned = new Dictionary<string, McpServer>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in _servers.Values)
            if (!string.IsNullOrWhiteSpace(server.Name))
                owned[server.Name!] = server;

        await _settings.UpdateAsync(current =>
        {
            foreach (var server in current.McpServers ?? Array.Empty<McpServer>())
            {
                if (server.Name is null || !owned.TryGetValue(server.Name, out var live))
                    continue;
                server.Tools = live.Tools;
                server.Stale = live.Stale;
                server.LastSyncUtc = live.LastSyncUtc;
                server.LastError = live.LastError;
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns tool definitions in the Ollama/OpenAI tool schema format for the /api/chat "tools" field.
    /// Includes both external MCP server tools and built-in VS tools.
    /// </summary>
    public IReadOnlyList<JsonObject> GetAvailableToolDefinitions(IReadOnlyList<string>? allowedNames = null)
    {
        var result = new List<JsonObject>();

        // Built-in VS tools: expose the shipped definitions, honoring the per-tool enable flags
        // persisted under the synthetic "builtin" server. When that entry has not been saved yet
        // (or the built-in server is absent) every built-in tool stays enabled, preserving prior behavior.
        var builtinEntry = (_loadedSettings?.McpServers ?? Array.Empty<McpServer>())
            .FirstOrDefault(s => string.Equals(s.Transport, "builtin", StringComparison.OrdinalIgnoreCase));
        if (builtinEntry is null || builtinEntry.Enabled)
        {
            var builtinEnabledByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in builtinEntry?.Tools ?? Array.Empty<McpTool>())
                if (!string.IsNullOrWhiteSpace(t.Name))
                    builtinEnabledByName[t.Name!] = t.Enabled;

            foreach (var tool in BuiltInVsTools.GetToolDefinitions())
            {
                if (string.IsNullOrWhiteSpace(tool.Name)) continue;
                if (builtinEnabledByName.TryGetValue(tool.Name!, out var en) && !en) continue;
                if (allowedNames is not null && !allowedNames.Contains(tool.Name))
                    continue;
                result.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = tool.Schema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    }
                });
            }
        }

        // Add external MCP server tools
        foreach (var server in _servers.Values)
        {
            if (!server.Enabled) continue;
            foreach (var tool in server.Tools ?? Array.Empty<McpTool>())
            {
                if (!tool.Enabled) continue;
                var runtimeName = $"{server.Name}-{tool.Name}";
                if (allowedNames is not null && !allowedNames.Contains(runtimeName) && !allowedNames.Contains(tool.Name ?? ""))
                    continue;
                result.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = runtimeName,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = tool.Schema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    }
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Executes a tool by routing to the correct MCP server based on the "<server>-<tool>" name prefix.
    /// Built-in VS tools (prefixed with "vs_") are handled directly without external server routing.
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, string? argumentsJson, CancellationToken ct = default)
    {
        // Check if this is a built-in VS tool
        if (toolName.StartsWith("vs_", StringComparison.Ordinal))
        {
            return await BuiltInVsTools.ExecuteAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
        }

        // Route by the exact "<server-key>-<tool-name>" composite the definitions advertise.
        // Splitting on the first dash breaks when a server name itself contains a dash
        // (e.g. "AI-on-Proxy"), so instead match the full prefix per enabled server - longest
        // server name first so a name that is a prefix of another resolves to the longer one.
        var enabledServers = _servers.Values.Where(s => s.Enabled).ToList();
        foreach (var server in enabledServers.OrderByDescending(s => s.Name?.Length ?? 0))
        {
            var prefix = $"{server.Name}-";
            if (string.IsNullOrEmpty(server.Name) || !toolName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var actualToolName = toolName.Substring(prefix.Length);
            if ((server.Tools ?? Array.Empty<McpTool>()).Any(t => t.Name == actualToolName))
                return await ExecuteOnServerAsync(server, actualToolName, argumentsJson, ct).ConfigureAwait(false);
        }

        // Fallback: a bare tool name with no server prefix.
        foreach (var server in enabledServers)
        {
            if ((server.Tools ?? Array.Empty<McpTool>()).Any(t => t.Name == toolName))
                return await ExecuteOnServerAsync(server, toolName, argumentsJson, ct).ConfigureAwait(false);
        }

        return $"Tool '{toolName}' not found in any enabled MCP server.";
    }

    private async Task<string> ExecuteOnServerAsync(McpServer server, string toolName, string? argsJson, CancellationToken ct)
    {
        try
        {
            if (server.Transport == "http" && !string.IsNullOrWhiteSpace(server.Url))
            {
                return await ExecuteToolHttpAsync(server.Url!, server.ApiKey, toolName, argsJson, ct).ConfigureAwait(false);
            }
            if (server.Transport == "stdio" && !string.IsNullOrWhiteSpace(server.Command))
            {
                return await ExecuteToolStdioAsync(server, toolName, argsJson, ct).ConfigureAwait(false);
            }
            return $"Server '{server.Name}' has no supported transport.";
        }
        catch (Exception ex)
        {
            DebugLog.Error($"MCP tool '{toolName}' on server '{server.Name}' failed.", ex);
            return $"Tool execution failed: {ex.Message}";
        }
    }

    // --- HTTP Streamable transport (JSON-RPC over /mcp) ---

    private const string McpSessionIdHeader = "Mcp-Session-Id";

    /// <summary>
    /// Tracks an MCP session: the (shared) HttpClient and the session ID returned by initialize.
    /// The client is owned by <see cref="SharedHttpClients"/>, so a session never disposes it.
    /// </summary>
    private sealed class McpHttpSession
    {
        public HttpClient Http { get; }
        public string? SessionId { get; set; }

        public McpHttpSession(HttpClient http) => Http = http;
    }

    /// <summary>
    /// Shared HttpClient cache keyed by endpoint + credential. Creating a client per MCP
    /// operation burned an ephemeral socket each time (TIME_WAIT churn) on busy sessions;
    /// cached clients live for the process lifetime and reuse pooled connections. Per-session
    /// state (Mcp-Session-Id) therefore rides on individual requests, never on
    /// DefaultRequestHeaders, so concurrent sessions cannot corrupt each other.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> SharedHttpClients =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the shared HttpClient for the given MCP endpoint, created on first use with
    /// optional Bearer auth.
    /// </summary>
    private static HttpClient GetSharedHttpClient(string url, string? apiKey) =>
        SharedHttpClients.GetOrAdd(url + "|" + (apiKey ?? string.Empty), _ =>
        {
            var http = new HttpClient { BaseAddress = new Uri(url) };
            HttpHelpers.SetBearerAuth(http, apiKey);
            return http;
        });

    /// <summary>Builds the MCP <c>initialize</c> JSON-RPC request body.</summary>
    private static JsonObject CreateInitializeRequest(int id = 0) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["clientInfo"] = new JsonObject { ["name"] = "Kaeo LLM Proxy", ["version"] = "1.0" },
            ["capabilities"] = new JsonObject(),
        }
    };

    /// <summary>Builds the MCP <c>notifications/initialized</c> notification body (no id).</summary>
    private static JsonObject CreateInitializedNotification() => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/initialized"
    };

    /// <summary>Builds a <c>tools/list</c> JSON-RPC request body.</summary>
    private static JsonObject CreateToolsListRequest(int id = 1) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/list",
        ["params"] = new JsonObject()
    };

    /// <summary>Builds a <c>tools/call</c> JSON-RPC request body.</summary>
    private static JsonObject CreateToolsCallRequest(string toolName, string? argsJson, int id = 2) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = argsJson is null ? new JsonObject() : JsonNode.Parse(argsJson)
        }
    };

    /// <summary>Parses a <c>tools/list</c> JSON-RPC response into enabled <see cref="McpTool"/> entries.</summary>
    private static List<McpTool> ParseToolsList(JsonNode? response)
    {
        var tools = new List<McpTool>();
        if (response?["result"]?["tools"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                tools.Add(new McpTool
                {
                    Name = t?["name"]?.GetValue<string>(),
                    Description = t?["description"]?.GetValue<string>(),
                    Schema = t?["inputSchema"],
                    Enabled = true
                });
            }
        }
        return tools;
    }

    /// <summary>
    /// Parses an SSE-framed response body and extracts the JSON payload from the first "data:" line.
    /// MCP Streamable HTTP wraps JSON-RPC responses in SSE frames like:
    ///   event: message
    ///   data: {"jsonrpc":"2.0",...}
    /// </summary>
    private static JsonNode? ParseSseResponse(string sseBody)
    {
        if (string.IsNullOrWhiteSpace(sseBody))
            return null;

        // Look for "data:" lines and extract the JSON payload
        foreach (var line in sseBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var json = trimmed.Substring("data:".Length).Trim();
                if (!string.IsNullOrEmpty(json))
                    return JsonNode.Parse(json);
            }
        }

        // Fallback: try parsing the whole body as JSON (some servers may not use SSE framing)
        if (sseBody.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return JsonNode.Parse(sseBody);

        return null;
    }

    /// <summary>
    /// Sends a JSON-RPC POST to the MCP endpoint and returns the parsed response body.
    /// Handles SSE-framed responses and tracks the session ID.
    /// </summary>
    private static async Task<JsonNode?> SendJsonRpcAsync(McpHttpSession session, JsonObject body, CancellationToken ct)
    {
        // The client is shared across sessions, so the per-session ID must ride on the request
        // itself instead of the client's DefaultRequestHeaders.
        var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(session.SessionId))
            request.Headers.TryAddWithoutValidation(McpSessionIdHeader, session.SessionId);

        JsonNode? result;
        using (request)
        {
            using var resp = await session.Http.SendAsync(request, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Capture session ID from response if present
            if (resp.Headers.TryGetValues(McpSessionIdHeader, out var values))
                session.SessionId = values.FirstOrDefault();

            // Notifications return 202 Accepted with no body
            if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
                return null;

            var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Parse SSE-framed response
            result = ParseSseResponse(respText);
        }

        return result;
    }

    private static async Task<IReadOnlyList<McpTool>> PullToolsHttpAsync(string url, string? apiKey, CancellationToken ct)
    {
        var session = new McpHttpSession(GetSharedHttpClient(url, apiKey));

                // Perform initialize handshake and capture session ID
                await SendJsonRpcAsync(session, CreateInitializeRequest(), ct).ConfigureAwait(false);

                // Send initialized notification (no response expected)
                await SendJsonRpcAsync(session, CreateInitializedNotification(), ct).ConfigureAwait(false);

                // Now pull tools
                var doc = await SendJsonRpcAsync(session, CreateToolsListRequest(), ct).ConfigureAwait(false);
                return ParseToolsList(doc);
            }

    private static async Task<string> ExecuteToolHttpAsync(string url, string? apiKey, string toolName, string? argsJson, CancellationToken ct)
    {
        var session = new McpHttpSession(GetSharedHttpClient(url, apiKey));

                // Perform initialize handshake
                await SendJsonRpcAsync(session, CreateInitializeRequest(), ct).ConfigureAwait(false);

                // Send initialized notification
                await SendJsonRpcAsync(session, CreateInitializedNotification(), ct).ConfigureAwait(false);

                // Call the tool
                var doc = await SendJsonRpcAsync(session, CreateToolsCallRequest(toolName, argsJson), ct).ConfigureAwait(false);
                return doc?["result"]?.ToJsonString() ?? string.Empty;
            }

    // --- stdio transport (JSON-RPC over stdin/stdout of a child process) ---

        /// <summary>
        /// Spawns the configured command, sends MCP initialize + tools/list over stdin, and returns
        /// the tool definitions read from stdout. The process is killed after the exchange.
        /// </summary>
        private static async Task<IReadOnlyList<McpTool>> PullToolsStdioAsync(McpServer server, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                return Array.Empty<McpTool>();

            var args = server.Args ?? Array.Empty<string>();
            // net48: IsNullOrWhiteSpace has no nullable annotation, so assert non-null.
            var process = StartProcess(server.Command!, args);
            try
            {
                // Send initialize request
                await WriteJsonRpcAsync(process, CreateInitializeRequest(), ct).ConfigureAwait(false);
                await ReadJsonRpcAsync(process, expectedId: 0, ct).ConfigureAwait(false);

                // Send initialized notification
                await WriteJsonRpcAsync(process, CreateInitializedNotification(), ct).ConfigureAwait(false);

                // Send tools/list
                await WriteJsonRpcAsync(process, CreateToolsListRequest(), ct).ConfigureAwait(false);
                var toolsResponse = await ReadJsonRpcAsync(process, expectedId: 1, ct).ConfigureAwait(false);

                return ParseToolsList(toolsResponse);
            }
            finally
            {
                KillProcess(process);
            }
        }

        /// <summary>
        /// Spawns the configured command, sends MCP initialize + tools/call over stdin, returns the
        /// tool result from stdout. The process is killed after the exchange.
        /// </summary>
        private static async Task<string> ExecuteToolStdioAsync(McpServer server, string toolName, string? argsJson, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                return "No command configured for this stdio server.";

            var commandArgs = server.Args ?? Array.Empty<string>();
            // net48: IsNullOrWhiteSpace has no nullable annotation, so assert non-null.
            var process = StartProcess(server.Command!, commandArgs);
            try
            {
                // Send initialize
                await WriteJsonRpcAsync(process, CreateInitializeRequest(), ct).ConfigureAwait(false);
                await ReadJsonRpcAsync(process, expectedId: 0, ct).ConfigureAwait(false);

                // Send initialized notification
                await WriteJsonRpcAsync(process, CreateInitializedNotification(), ct).ConfigureAwait(false);

                // Send tools/call
                await WriteJsonRpcAsync(process, CreateToolsCallRequest(toolName, argsJson), ct).ConfigureAwait(false);
                var response = await ReadJsonRpcAsync(process, expectedId: 2, ct).ConfigureAwait(false);

                return response?["result"]?.ToJsonString() ?? string.Empty;
            }
            finally
            {
                KillProcess(process);
            }
        }

        private static Process StartProcess(string command, string[] args)
        {
            var info = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Join args with proper quoting
            if (args.Length > 0)
                info.Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var process = new Process { StartInfo = info };
            // Drain stderr continuously: MCP servers log there, and an unread redirected
            // stderr pipe fills its buffer and deadlocks the child mid-exchange.
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    DebugLog.Info($"[mcp:{command}] {e.Data}");
            };
            process.Start();
            process.BeginErrorReadLine();
            return process;
        }

        private static async Task WriteJsonRpcAsync(Process process, JsonObject body, CancellationToken ct)
        {
            var json = body.ToJsonString() + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Per-read timeout for stdio JSON-RPC responses.</summary>
        private static readonly TimeSpan StdioReadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Reads stdout lines until the JSON-RPC response with <paramref name="expectedId"/>
        /// arrives, skipping notifications, log noise, and responses to other requests. Each
        /// read is bounded by <see cref="StdioReadTimeout"/>; on expiry the child is killed so
        /// the blocked reader thread is released and no orphan process survives.
        /// </summary>
        private static async Task<JsonNode?> ReadJsonRpcAsync(Process process, int expectedId, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // net48: ReadLineAsync has no CancellationToken overload. Use Task.Run with
                // synchronous ReadLine; swallow read faults so killing the process on timeout
                // cannot surface later as an UnobservedTaskException.
                var readTask = Task.Run(() =>
                {
                    try { return process.StandardOutput.ReadLine(); }
                    catch (Exception) { return null; }
                });

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(StdioReadTimeout, delayCts.Token);
                var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
                if (completed != readTask)
                {
                    ct.ThrowIfCancellationRequested();
                    KillProcess(process);
                    throw new TimeoutException($"MCP stdio server did not respond within {StdioReadTimeout.TotalSeconds:0} seconds.");
                }

                // Release the pending delay timer promptly instead of letting it linger.
                delayCts.Cancel();

                var line = await readTask.ConfigureAwait(false);
                if (line is null)
                    throw new IOException("MCP stdio server closed its output stream before responding.");
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException) { continue; } // non-JSON log noise on stdout

                if (node is JsonObject obj
                    && obj["id"] is JsonValue idValue
                    && idValue.TryGetValue<int>(out var id)
                    && id == expectedId)
                {
                    return node;
                }
            }
        }

        private static void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Process already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
