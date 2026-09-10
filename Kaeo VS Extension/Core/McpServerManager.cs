using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
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
            _ = PullAndCacheAsync(server, ct);
        }

        // Start heartbeat monitoring
        StartHeartbeat();
    }

    /// <summary>
    /// Starts periodic heartbeat monitoring for all enabled servers.
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatTimer = new Timer(
            _ => _ = HeartbeatAsync(),
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
        using var session = new McpHttpSession(CreateHttpClient(url, apiKey));

        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["clientInfo"] = new JsonObject { ["name"] = "Kaeo LLM Proxy", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject(),
            }
        };

        var doc = await SendJsonRpcAsync(session, body, ct).ConfigureAwait(false);
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

    /// <summary>Saves the settings instance this manager loaded, so cached tools survive a restart.</summary>
    private async Task PersistLoadedAsync()
    {
        if (_loadedSettings is null)
            return;
        await _settings.SaveAsync(_loadedSettings).ConfigureAwait(false);
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

        // Parse "<server-key>-<tool-name>" or fall back to searching all servers.
        var dashIdx = toolName.IndexOf('-');
        if (dashIdx > 0)
        {
            var serverKey = toolName.Substring(0, dashIdx);
            var actualToolName = toolName.Substring(dashIdx + 1);
            if (_servers.TryGetValue(serverKey, out var server))
            {
                return await ExecuteOnServerAsync(server, actualToolName, argumentsJson, ct).ConfigureAwait(false);
            }
        }

        // Fallback: search all servers for a matching tool name.
        foreach (var server in _servers.Values)
        {
            if (!server.Enabled) continue;
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
    /// Tracks an MCP session: the HttpClient and the session ID returned by initialize.
    /// </summary>
    private sealed class McpHttpSession : IDisposable
    {
        public HttpClient Http { get; }
        public string? SessionId { get; set; }

        public McpHttpSession(HttpClient http) => Http = http;

        public void Dispose() => Http.Dispose();
    }

    /// <summary>
    /// Creates an HttpClient configured for the given MCP endpoint with optional Bearer auth.
    /// </summary>
    private static HttpClient CreateHttpClient(string url, string? apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(url) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return http;
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
        // Add session ID header if we have one
        if (!string.IsNullOrEmpty(session.SessionId))
        {
            session.Http.DefaultRequestHeaders.Remove(McpSessionIdHeader);
            session.Http.DefaultRequestHeaders.Add(McpSessionIdHeader, session.SessionId);
        }

        var resp = await session.Http.PostAsync("", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Capture session ID from response if present
        if (resp.Headers.TryGetValues(McpSessionIdHeader, out var values))
            session.SessionId = values.FirstOrDefault();

        // Notifications return 202 Accepted with no body
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            return null;

        var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Parse SSE-framed response
        return ParseSseResponse(respText);
    }

    private static async Task<IReadOnlyList<McpTool>> PullToolsHttpAsync(string url, string? apiKey, CancellationToken ct)
    {
        using var session = new McpHttpSession(CreateHttpClient(url, apiKey));

        // Perform initialize handshake and capture session ID
        var initBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["clientInfo"] = new JsonObject { ["name"] = "Kaeo LLM Proxy", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject(),
            }
        };

        var initDoc = await SendJsonRpcAsync(session, initBody, ct).ConfigureAwait(false);

        // Send initialized notification (no response expected)
        var notifiedBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        };
        await SendJsonRpcAsync(session, notifiedBody, ct).ConfigureAwait(false);

        // Now pull tools
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
            ["params"] = new JsonObject()
        };

        var doc = await SendJsonRpcAsync(session, body, ct).ConfigureAwait(false);
        var tools = new List<McpTool>();
        if (doc?["result"]?["tools"] is JsonArray arr)
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

    private static async Task<string> ExecuteToolHttpAsync(string url, string? apiKey, string toolName, string? argsJson, CancellationToken ct)
    {
        using var session = new McpHttpSession(CreateHttpClient(url, apiKey));

        // Perform initialize handshake
        var initBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["clientInfo"] = new JsonObject { ["name"] = "Kaeo LLM Proxy", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject(),
            }
        };
        await SendJsonRpcAsync(session, initBody, ct).ConfigureAwait(false);

        // Send initialized notification
        var notifiedBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        };
        await SendJsonRpcAsync(session, notifiedBody, ct).ConfigureAwait(false);

        // Call the tool
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argsJson is null ? new JsonObject() : JsonNode.Parse(argsJson)
            }
        };

        var doc = await SendJsonRpcAsync(session, body, ct).ConfigureAwait(false);
        return doc?["result"]?.ToJsonString() ?? string.Empty;
    }

    // --- stdio transport (JSON-RPC over stdin/stdout of a child process) ---

    private static async Task<IReadOnlyList<McpTool>> PullToolsStdioAsync(McpServer server, CancellationToken ct)
    {
        // Placeholder: spawn the command, send initialize + tools/list, read response.
        return Array.Empty<McpTool>();
    }

    private static async Task<string> ExecuteToolStdioAsync(McpServer server, string toolName, string? argsJson, CancellationToken ct)
    {
        // Placeholder: spawn the command, send tools/call, read response.
        return "stdio transport not yet implemented.";
    }
}
