using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.Tests.Conformance;

/// <summary>
/// Locates the vendored vendor specifications under the repository's <c>API Specs\</c> folder and
/// caches the parsed documents for the test run.
/// </summary>
/// <remarks>
/// The specs are repository content, not build output, so they are located by walking up from the
/// test binary to the directory that contains the solution file. Walking up rather than hard-coding
/// a relative hop count keeps this working from any target framework's output folder.
/// </remarks>
internal static class SpecCatalog
{
    private static readonly Lazy<string?> RepositoryRoot = new(FindRepositoryRoot);

    private static readonly Lazy<VendorSpec?> OpenAi = new(() => LoadIfPresent(
        Path.Combine("docs\\API Specs", "openai-openapi", "openai-openapi", "openapi.json")));

    private static readonly Lazy<VendorSpec?> Cohere = new(() => LoadIfPresent(
        Path.Combine("docs\\API Specs", "cohere-developer-experience", "cohere-openapi.yaml")));

    /// <summary>
    /// The repository root that contains the solution file, or null when the tests run from a
    /// location with no solution above them (CI container, published binary layout). Callers
    /// treat a null root like missing spec content and skip.
    /// </summary>
    public static string? Root => RepositoryRoot.Value;

    /// <summary>The vendored OpenAI OpenAPI document, or null when it is not present.</summary>
    public static VendorSpec? OpenAiSpec => OpenAi.Value;

    /// <summary>
    /// True when the OpenAI spec is available. Tests that need it skip rather than fail when the
    /// spec folder has not been checked out, so the suite still runs on a partial clone.
    /// </summary>
    public static bool HasOpenAiSpec => OpenAiSpec is not null;

    /// <summary>
    /// True when the Cohere spec file exists. It is published as YAML, which this harness does not
    /// parse, so its presence is asserted without attempting to read it.
    /// </summary>
    public static bool HasCohereSpec => Cohere.Value is not null || (Root is not null && File.Exists(Path.Combine(
        Root, "docs\\API Specs", "cohere-developer-experience", "cohere-openapi.yaml")));

    private static VendorSpec? LoadIfPresent(string relativePath)
    {
        if (Root is null)
            return null;

        string path = Path.Combine(Root, relativePath);
        return File.Exists(path) ? VendorSpec.Load(path) : null;
    }

    /// <summary>
    /// Walks up from the test output directory to the first directory containing a <c>.slnx</c>
    /// or <c>.sln</c> file. Returns null rather than throwing when there is none: spec-backed
    /// conformance tests then skip through <see cref="HasOpenAiSpec"/> instead of erroring the
    /// whole suite on machines without a checkout (CI containers, published layouts).
    /// </summary>
    private static string? FindRepositoryRoot()
    {
        try
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (directory.EnumerateFiles("*.slnx").Any() || directory.EnumerateFiles("*.sln").Any())
                    return directory.FullName;

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // An unreadable ancestor directory is the same as "no root found" — skip, don't crash.
        }

        return null;
    }

    /// <summary>Parses one JSON document into a node for validation.</summary>
    public static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}