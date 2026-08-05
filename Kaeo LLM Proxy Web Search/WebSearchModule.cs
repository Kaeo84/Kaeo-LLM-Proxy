using Kaeo.LlmProxy.Modules;
using Kaeo.LlmProxy.WebSearch.Core.Services;
using Kaeo.LlmProxy.WebSearch.UI;

namespace Kaeo.LlmProxy.WebSearch;

/// <summary>
/// The Web Search sub-module entry point discovered by the host via <see cref="IKaeoModule"/>.
/// Contributes the web_search/web_fetch tools to the host's built-in MCP server and persists the
/// provider catalog, domain rules, and feature settings in the shared application database.
/// </summary>
public sealed class WebSearchModule : IKaeoModule, IMcpToolModule
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

        ApplySchema(context.Database);

        _repository = new WebSearchRepository(context.Database);
        _repository.SeedDefaultProviders();

        _webSearchService = new WebSearchService(_repository, context.Secrets);
    }

    public System.Windows.Forms.TabPage CreateConfigPage() => new WebSearchConfigPage(this);

    /// <summary>Tool targets for the host's MCP server; enablement is read live per call.</summary>
    public IReadOnlyList<object> CreateMcpToolTargets() =>
        [new WebSearchTools(_webSearchService!, _repository!)];

    private static void ApplySchema(IModuleDatabase database)
    {
        using Stream stream = typeof(WebSearchModule).Assembly
            .GetManifestResourceStream("Kaeo.LlmProxy.WebSearch.Infrastructure.web_search_schema.sql")
            ?? throw new InvalidOperationException(
                "Embedded schema resource 'web_search_schema.sql' is missing.");

        using var reader = new StreamReader(stream);
        database.ExecuteSchemaScript(reader.ReadToEnd());
    }
}
