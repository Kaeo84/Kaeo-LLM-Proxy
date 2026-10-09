using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// Single source of truth for Code Vector Store setting descriptions. Consumed by the
/// config page hover tooltips, the settings help modal, and the module help tab so all
/// three stay consistent.
/// </summary>
internal static class CodeVectorSettingsHelp
{
    public readonly record struct Entry(string Setting, string Description);

    public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
    {
        new("Vector Database", "File path of the SQLite database holding collections, chunks, and mirror registrations. Empty uses the module data directory. Pointing at a different path selects a different (possibly empty) store; the vector engine restarts on change."),
        new("Backend", "Embedding backend: Remote calls an HTTP /v1/embeddings endpoint on a server, ONNX runs embeddings locally on CPU from model.onnx + vocab.txt. Switching backends changes vector dimensions, so reindexing is required for search."),
        new("Remote URL", "Base URL of a remote embedding server exposing OpenAI-compatible /v1/embeddings and /v1/models endpoints."),
        new("Remote Model", "Identifier of the embedding model used on the remote server. Must produce the same vector dimension as the indexed content; use code_reindex after changing it."),
        new("Fetch Models", "Pulls the list of models available on the remote server into the Model dropdown."),
        new("Model Info", "Shows metadata (dimension, context) for the selected remote model."),
        new("Test Connection", "Verifies the remote server is reachable and the selected credential is accepted."),
        new("Credential", "Name of a credential in the host's central credential store. Its secret is sent as a bearer token to the remote server; empty means no authentication."),
        new("Timeout (s)", "Per-request HTTP timeout in seconds for remote embedding calls (5-300)."),
        new("Parallelism", "Maximum concurrent embedding requests to the remote backend (1-16). Controls both the parallel file workers and the in-flight batch requests."),
        new("ONNX Model Folder", "Directory containing model.onnx and vocab.txt for local CPU embedding."),
        new("ONNX Max Sequence", "Maximum token sequence length passed to the ONNX model; longer text is truncated before embedding."),
        new("ONNX Threads", "Number of CPU threads the ONNX runtime uses for inference."),
        new("Chunk Lines", "Source lines per embedding chunk. Smaller gives finer-grained search results but more vectors and more embedding work."),
        new("Overlap Lines", "Lines shared between adjacent chunks so search context is not cut at chunk boundaries."),
        new("Max File (KB)", "Files larger than this are skipped during indexing."),
        new("Default Top K", "Number of results the code_search MCP tool returns when the caller does not specify topK."),
        new("Git Sync (min)", "Minutes between periodic pulls of registered git mirrors. 0 disables periodic sync (manual/on-demand only)."),
        new("Log Level", "Verbosity of this module's MCP activity log: None = nothing, Connectivity = connect/disconnect only, Full = every tool call with request/response details."),
        new("Tools", "Enables or disables individual MCP tools exposed to MCP clients. Changes take effect for new MCP sessions."),
    }.AsReadOnly();

    /// <summary>Looks up the description for a setting by its label, case-insensitive. Returns null when unknown.</summary>
    public static string? Describe(string setting)
    {
        foreach (var e in Entries)
        {
            if (string.Equals(e.Setting, setting, StringComparison.OrdinalIgnoreCase))
                return e.Description;
        }
        return null;
    }

    /// <summary>Builds the full settings reference text for the modal and help tab.</summary>
    public static string BuildText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CODE VECTOR STORE — SETTINGS REFERENCE");
        sb.AppendLine();
        foreach (var e in Entries)
        {
            sb.AppendLine($"{e.Setting}:");
            sb.AppendLine($"  {e.Description}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
