using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Module.WebSearch;

/// <summary>
/// The Web Search sub-module entry point discovered by the host via <see cref="IKaeoModule"/>.
/// Contributes the web_search/web_fetch tools to the host's built-in MCP server and persists the
/// provider catalog, domain rules, and feature settings in the shared application database.
/// </summary>
public sealed partial class WebSearchModule : IKaeoModule, IMcpToolModule
{
    public const string Version = "1.0.0";

    private ModuleContext? _context;
    private WebSearchRepository? _repository;
    private WebSearchService? _webSearchService;

    public string Id => "kaeo.websearch";

    public string Name => "Web Search";

    string IKaeoModule.Version => Version;

    public string Description =>
        "Safe, configurable Web Search tools (web_search/web_fetch) for the built-in MCP server.";

    internal WebSearchRepository Repository =>
        _repository ?? throw new InvalidOperationException("Module not initialized.");

    internal WebSearchService WebSearch =>
        _webSearchService ?? throw new InvalidOperationException("Module not initialized.");

    internal ISecretProvider Secrets =>
        _context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");

    public void Initialize(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        _repository = new WebSearchRepository(context.Database);

        // H3: a sharing violation (another instance holds the database) must not crash module
        // discovery. Log and continue; the tools report a friendly error per call until the
        // database becomes available again.
        try
        {
            ApplySchema(context.Database);
            _repository.SeedDefaultProviders();
        }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
        {
            Serilog.Log.Warning(ex, "Web Search module could not initialize its database schema; it will retry on demand.");
        }

        _webSearchService = new WebSearchService(_repository, context.Secrets);
    }

    /// <summary>
    /// Tool targets for the host's MCP server; enablement is read live per call. The session
    /// info is not needed by this module's targets.
    /// </summary>
    public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) =>
        [new WebSearchTools(_webSearchService!, _repository!)];

    /// <summary>
    /// Baseline schema for the module's tables, applied during initialization. Idempotent:
    /// safe to run on every startup.
    /// </summary>
    private const string SchemaScript = """
-- Kaeo LLM Proxy Module Web Search module baseline schema.
-- Idempotent: safe to run on every startup.

-- Web search provider catalog with per-provider settings.
-- Exactly one row per provider kind; enabled flag toggles participation in queries.
CREATE TABLE IF NOT EXISTS mcp_web_search_providers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    is_enabled INTEGER NOT NULL DEFAULT 0,
    endpoint TEXT NOT NULL,
    credential_name TEXT NULL
);

-- Domain allow/deny rules for web_search/web_fetch.
-- rule_type: 0 = allow, 1 = deny. An allowlist with any entry restricts everything else.
CREATE TABLE IF NOT EXISTS mcp_domain_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_type INTEGER NOT NULL,
    pattern TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_mcp_domain_rules_unique
    ON mcp_domain_rules (rule_type, pattern);

-- Key/value settings for the Web Search feature (tool toggles, result limits, timeouts,
-- response size cap, allow-local-network opt-in).
CREATE TABLE IF NOT EXISTS mcp_web_search_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
""";

    private static void ApplySchema(IModuleDatabase database) => database.ExecuteSchemaScript(SchemaScript);
}
