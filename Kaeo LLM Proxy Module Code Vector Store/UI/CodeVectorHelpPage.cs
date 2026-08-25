namespace Kaeo.LlmProxy.Module.CodeVector;

internal static class CodeVectorHelpPage
{
    public static System.Windows.Forms.TabPage Create()
    {
        var page = new System.Windows.Forms.TabPage { Text = "Code Vector Store", Padding = new System.Windows.Forms.Padding(8) };
        var body = new System.Windows.Forms.TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            BackColor = System.Drawing.SystemColors.Window,
            Text = HelpText,
        };
        page.Controls.Add(body);
        return page;
    }

    private const string HelpText = """
        CODE VECTOR STORE MODULE
        Provides embeddings and a vector store for code search via MCP tools.
        TOOLS:
          code_search            Semantic search across indexed code chunks.
          code_index             Index/refresh a single file (agent push).
          code_sync_repo         Register and sync a git mirror or watched directory.
          code_list_collections  List collections with file/chunk counts.
          code_status            Backend, collection, and mirror status.
          code_remove            Delete a collection or a path prefix within one.
          code_reindex           Re-embed every file in a collection.
        BACKENDS: Remote (HTTP /v1/embeddings), Local ONNX (CPU, model.onnx + vocab.txt)
        SYNC: Agent push via code_index, Git mirror via LibGit2Sharp with periodic pull.
        """;
}
