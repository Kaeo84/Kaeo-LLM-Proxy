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

    /// <summary>The settings instance <see cref="InitializeAsync"/> loaded; pulls are cached back into it.</summary>
    private ExtensionSettings? _loadedSettings;

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
        _servers.Clear();
        foreach (var server in all.McpServers ?? Array.Empty<McpServer>())
        {
            if (!server.Enabled || string.IsNullOrWhiteSpace(server.Name))
                continue;
            _servers[server.Name!] = server;
            _ = PullAndCacheAsync(server, ct);
        }
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
        catch
        {
            server.Stale = true;
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
    /// </summary>
    public IReadOnlyList<JsonObject> GetAvailableToolDefinitions(IReadOnlyList<string>? allowedNames = null)
    {
        var result = new List<JsonObject>();
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
                        ["parameters"] = tool.Schema ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    }
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Executes a tool by routing to the correct MCP server based on the "<server>-<tool>" name prefix.
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, string? argumentsJson, CancellationToken ct = default)
    {
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
            return $"Tool execution failed: {ex.Message}";
        }
    }

    // --- HTTP Streamable transport (JSON-RPC over /mcp) ---

    private static async Task<IReadOnlyList<McpTool>> PullToolsHttpAsync(string url, string? apiKey, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
            ["params"] = new JsonObject()
        };

        var resp = await http.PostAsync("", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        // ReadAsStringAsync(ct) is net5+; use the parameterless overload for net48.
        var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = JsonNode.Parse(respText);
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
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

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

        var resp = await http.PostAsync("", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        // ReadAsStringAsync(ct) is net5+; use the parameterless overload for net48.
        var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = JsonNode.Parse(respText);
        return doc?["result"]?.ToJsonString() ?? respText;
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
