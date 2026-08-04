using System.Net.Sockets;
using System.Reflection;
using Kaeo.LlmProxy.Mcp.Core.Models;
using Kaeo.LlmProxy.Mcp.Core.Services;
using Kaeo.LlmProxy.Mcp.Infrastructure;
using Kaeo.LlmProxy.Mcp.UI;
using Kaeo.LlmProxy.Modules;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using System.Net;

namespace Kaeo.LlmProxy.Mcp;

/// <summary>
/// The MCP module entry point discovered by the host via <see cref="IKaeoModule"/>. Runs an
/// MCP Streamable HTTP server with safe, configurable Web Search tools (web_search/web_fetch),
/// persists all configuration in the shared application database, and surfaces its OpenAPI
/// document to the host's API explorer.
/// </summary>
public sealed class McpModule : IKaeoModule, IRunnableModule, IApiExplorerDocumentsProvider
{
    public const string Version = "1.0.0";

    private ModuleContext? _context;
    private McpSettingsRepository? _repository;
    private WebSearchService? _webSearchService;
    private McpServerHost? _host;
    private McpApiExplorer? _apiExplorer;
    private string _status = "Stopped";

    public string Id => "kaeo.mcp";

    public string Name => "MCP Server";

    string IKaeoModule.Version => Version;

    public string Description => "MCP (Model Context Protocol) server exposing safe, configurable Web Search tools.";

    public bool IsRunning => _host?.IsRunning == true;

    public event EventHandler<string>? StatusChanged;

    /// <summary>Current display status (also delivered via <see cref="StatusChanged"/>).</summary>
    public string Status => _status;

    /// <summary>Client-facing MCP endpoint URL when running; otherwise null.</summary>
    public string? EndpointUrl => _host is { IsRunning: true } ? _host.EndpointUrl : null;

    /// <summary>Module Scalar explorer URL when running; otherwise null.</summary>
    public string? ScalarUrl => EndpointUrl is { } endpoint
        ? endpoint[..^McpServerHost.McpPath.Length] + McpServerHost.ScalarPath
        : null;

    internal McpSettingsRepository Repository => _repository ?? throw new InvalidOperationException("Module not initialized.");

    internal ISecretProvider Secrets => _context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");

    // ── IKaeoModule ─────────────────────────────────────────────────────────

    public void Initialize(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        ApplySchema(context.Database);

        _repository = new McpSettingsRepository(context.Database);
        _repository.SeedDefaultProviders();

        _webSearchService = new WebSearchService(_repository, context.Secrets);
    }

    public System.Windows.Forms.TabPage CreateConfigPage() => new McpConfigPage(this);

    // ── IRunnableModule ─────────────────────────────────────────────────────

    /// <summary>
    /// Starts the MCP server when the persisted enabled flag is set; modules decide their own
    /// auto-start policy, so a disabled module is a no-op here.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null || _repository is null || _webSearchService is null)
            throw new InvalidOperationException("Module not initialized.");

        McpServerSettings settings = _repository.LoadServerSettings();
        if (!settings.Enabled)
        {
            RaiseStatus("Disabled");
            return;
        }

        try
        {
            if (_host is { IsRunning: true })
                await _host.StopAsync();

            // Recreate the host so fresh endpoint/auth settings always apply.
            _host = new McpServerHost(settings, _context.Secrets, BuildServerOptions);
            _apiExplorer = new McpApiExplorer(_host, _context.Host);
            _host.ApiExplorer = _apiExplorer;

            await _host.StartAsync(cancellationToken);
            RaiseStatus($"Running at {_host.EndpointUrl}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpListenerException)
        {
            RaiseStatus($"Failed to start: {ex.Message}");
            Log.Error(ex, "MCP module failed to start");
        }
    }

    public async Task StopAsync()
    {
        if (_host is { IsRunning: true })
            await _host.StopAsync();

        RaiseStatus("Stopped");
    }

    /// <summary>
    /// Re-reads persisted server settings and restarts (or stops) the listener accordingly.
    /// Used by the configuration UI after the user edits endpoint or authentication settings.
    /// </summary>
    public async Task ApplyServerSettingsAsync()
    {
        if (_repository is null)
            return;

        if (_repository.LoadServerSettings().Enabled)
        {
            await StartAsync();
        }
        else if (_host is { IsRunning: true })
        {
            await StopAsync();
        }
        else
        {
            RaiseStatus("Disabled");
        }
    }

    // ── IApiExplorerDocumentsProvider ───────────────────────────────────────

    public IReadOnlyList<ExplorerDocument> GetExplorerDocuments() =>
        _apiExplorer?.GetExplorerDocuments() ?? [];

    // ── Internals ───────────────────────────────────────────────────────────

    private static void ApplySchema(IModuleDatabase database)
    {
        using Stream stream = typeof(McpModule).Assembly
            .GetManifestResourceStream("Kaeo.LlmProxy.Mcp.Infrastructure.mcp_schema.sql")
            ?? throw new InvalidOperationException("Embedded schema resource 'mcp_schema.sql' is missing.");

        using var reader = new StreamReader(stream);
        database.ExecuteSchemaScript(reader.ReadToEnd());
    }

    /// <summary>
    /// Builds fresh server options for each new session: server info plus only the tools that
    /// are currently enabled. Tool enablement is additionally enforced at invocation time.
    /// </summary>
    private McpServerOptions BuildServerOptions()
    {
        WebSearchSettings webSettings = _repository!.LoadWebSearchSettings();

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Kaeo LLM Proxy MCP", Version = Version },
            ServerInstructions =
                "Provides Web Search tools: use web_search to find pages and web_fetch to read a page's text content.",
        };

        var tools = new WebSearchTools(_webSearchService!, _repository);

        foreach (MethodInfo method in typeof(WebSearchTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            McpServerToolAttribute? attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null)
                continue;

            string toolName = attribute.Name ?? method.Name;
            if (string.Equals(toolName, "web_search", StringComparison.OrdinalIgnoreCase) && !webSettings.WebSearchToolEnabled)
                continue;
            if (string.Equals(toolName, "web_fetch", StringComparison.OrdinalIgnoreCase) && !webSettings.WebFetchToolEnabled)
                continue;

            McpServerPrimitiveCollection<McpServerTool> toolCollection =
                options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            toolCollection.Add(McpServerTool.Create(method, tools));
        }

        return options;
    }

    private void RaiseStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
