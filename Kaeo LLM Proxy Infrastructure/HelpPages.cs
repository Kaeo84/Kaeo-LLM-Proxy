namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Builds the host-owned pages of the Help tab: one introductory blurb per dashboard tab plus
/// the MCP page with Server/Modules sub-pages. Module-provided help pages are injected by
/// MainForm into the Modules page via <see cref="Modules.IHelpModule"/>.
/// </summary>
internal static class HelpPages
{
    /// <summary>A scrollable, read-only text page that follows the current color mode.</summary>
    internal static TabPage TextPage(string title, string body)
    {
        TextBox text = new()
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            Text = body,
        };

        TabPage page = new() { Text = title, Padding = new Padding(8) };
        page.Controls.Add(text);
        return page;
    }

    internal const string Dashboard = """
        The Dashboard is the at-a-glance view for both services: live CPU/memory sampling, the Proxy Status group (listener state, running listen IP/port, request counters, and Start / Stop / Restart), and the MCP Status group with the same controls and counters for the built-in MCP server.

        Counters are tracked per service; each group's Reset Stats clears only that service's counters. The Logs tab keeps the matching per-service request lists.
        """;

    internal const string Logs = """
        The Logs tab lists captured requests in two sub-tabs: Proxy (method, path, model, status, duration, tokens, and bytes transferred) and MCP (method, path, status, duration, and bytes).

        Auto-refresh polls at the selected interval; Refresh forces an immediate update of both lists and Clear empties the selected sub-tab's list. Double-clicking an entry (or Log Details) opens the full captured detail, including request/response bodies when detail collection is enabled.
        """;

    internal const string Settings = """
        Settings controls how the proxy listens and behaves. The Listener group (port/address) saves explicitly and requires a proxy restart; everything else persists immediately when changed.

        Model mappings map an exposed proxy model name to a specific upstream server and model, with per-model sampling, timeout, credential, thinking, and summarization options. Credentials stores named API keys encrypted at rest that mappings and modules reference by name.

        "Run as administrator on launch" re-launches the app elevated at the next start (release builds only) so non-localhost listener bindings are permitted; debug builds never elevate and listen on localhost instead.
        """;

    internal const string Instructions = """
        Instruction sets are named text blocks that can be prepended to requests. Create, edit, and remove sets here, then reference a set from a model mapping's configuration to inject it for that model only.
        """;

    internal const string Credentials = """
        Credentials stores named secrets (for example upstream API keys) encrypted at rest in the application database. Model mappings and module providers reference credentials by name, so a secret is entered once and never duplicated into mappings. Editing a credential re-encrypts it with the session passphrase.
        """;

    internal const string McpServer = """
        The Server page controls the built-in MCP Streamable HTTP endpoint (default http://localhost:8388/mcp): enable/disable, port, listen address, and live status. Apply & Restart rebinds the listener with the current values. The listen address dropdown lists localhost, all interfaces (0.0.0.0), and the machine's current NIC addresses; non-localhost bindings require elevation or a URL reservation (see Settings). The endpoint is also served at the server root (http://localhost:8388) for clients that only accept a base URL, such as GitHub Copilot in Visual Studio.

        While running, the MCP OpenAPI document also appears in the proxy API explorer dropdown, and /scalar on the MCP port serves an interactive explorer. Loaded MCP sub-modules contribute their tools to this server automatically.
        """;

    internal const string McpModules = """
        The Modules page is the registry for importable MCP sub-modules. Import a module assembly (.dll) built against the Kaeo LLM Proxy Modules contracts; the host loads it in an isolated context, applies its schema, and injects its configuration page under this MCP tab.

        Enable/Disable toggles a registered module without removing it; Remove unregisters it. A module that fails to load records its error in the State column instead of blocking the host. Loaded modules contribute MCP tools to the Server page's endpoint and can add pages to the Help tab.
        """;

    internal const string Test = """
        The Test console sends chat requests through the proxy using your configured model mappings, with temperature and repeat-penalty controls and streamed output. It is the quickest way to verify a mapping, upstream connectivity, and thinking/summarization behavior without an external client.
        """;

    internal const string Heartbeats = """
        Heartbeats shows per-model streaming heartbeat activity: while the proxy waits on a long-thinking upstream it emits harmless keep-alive frames so clients do not time out. Use this view to confirm a request is alive before the first tokens arrive, and to tune the heartbeat interval in Settings.
        """;

    internal const string ModulesPlaceholder = """
        No loaded module provides help content yet. Import an MCP sub-module that implements IHelpModule (for example Kaeo LLM Proxy Web Search) and its documentation appears here.
        """;
}
