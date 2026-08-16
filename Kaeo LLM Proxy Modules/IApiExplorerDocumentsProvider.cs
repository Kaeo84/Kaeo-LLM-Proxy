namespace Kaeo.LlmProxy.Modules;

/// <summary>An OpenAPI document a module exposes for API explorer integration.</summary>
public sealed class ExplorerDocument
{
    public ExplorerDocument(string label, string specUrl)
    {
        Label = label;
        SpecUrl = specUrl;
    }

    /// <summary>Display label for the explorer's document dropdown.</summary>
    public string Label { get; }

    /// <summary>Absolute URL where the OpenAPI document is served.</summary>
    public string SpecUrl { get; }
}

/// <summary>
/// Optional contract for modules that serve OpenAPI-described HTTP endpoints. The host's API
/// explorer lists these documents alongside its own so both can be browsed from a single
/// dropdown; module explorers may use the same mechanism in reverse.
/// </summary>
public interface IApiExplorerDocumentsProvider
{
    /// <summary>
    /// Returns the OpenAPI documents this module currently exposes. Called at page-render time;
    /// implementations must be cheap and must not throw when the module's service is stopped.
    /// </summary>
    IReadOnlyList<ExplorerDocument> GetExplorerDocuments();
}
