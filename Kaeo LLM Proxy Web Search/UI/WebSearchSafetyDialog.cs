namespace Kaeo.LlmProxy.WebSearch.UI;

/// <summary>
/// Modal reference documenting every precaution the module builds around web search, including
/// the prompt-injection and SSRF defenses. Opened from the info icon on the configuration page.
/// </summary>
internal sealed class WebSearchSafetyDialog : Form
{
    public WebSearchSafetyDialog()
    {
        Text = "Web Search Safety Precautions";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 600);

        TextBox body = new()
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            TabIndex = 1,
            Text = SafetyText,
        };

        Button ok = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 90,
            TabIndex = 0,
        };

        FlowLayoutPanel footer = new()
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        footer.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;

        Controls.Add(body);
        Controls.Add(footer);
    }

    private const string SafetyText = """
        Web search runs against the public internet, so every layer below assumes fetched pages may be hostile — including pages that try to manipulate the AI itself. These are the precautions built into the code:

        1. Deny-first domain policy
           What it is: your allow/deny domain rules, checked before any request goes out.
           How it works: every URL is matched against the rules on this page; deny always wins, and as soon as any allow rule exists, every domain not listed becomes unreachable.

        2. SSRF guard (internal-network protection)
           What it is: blocks server-side request forgery into your own network.
           How it works: only http/https URLs are accepted; the host name is DNS-resolved and every returned address is checked — loopback, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, the cloud metadata range), and CGNAT (100.64/10) are refused unless "Allow local/private networks" is checked. A name resolving to both public and private addresses is treated as private.

        3. Redirect validation on every hop
           What it is: manual redirect following with re-validation.
           How it works: automatic HTTP redirects are disabled; up to 5 hops are followed by hand and each hop re-runs the domain policy and the SSRF guard before it is requested, so a public page cannot bounce the fetch onto an internal address.

        4. Size and time limits
           What it is: the Max page size and Timeout limits on this page.
           How it works: responses are streamed and cut off at the byte cap, and every operation is cancelled at the timeout — bounding how much text a hostile page can inject and preventing hung or slow responses.

        5. Covert-channel stripping
           What it is: HTML-to-text conversion that removes content hidden from humans.
           How it works: script/style/template markup, HTML comments, human-hidden elements (hidden attribute, display:none, visibility:hidden, aria-hidden="true"), and invisible unicode (zero-width characters, directional marks, soft hyphens) are stripped — the usual carriers of instructions hidden from you but visible to the AI.

        6. Untrusted-content framing (prompt-injection mitigation)
           What it is: every web_search/web_fetch result is labelled untrusted data before the AI sees it.
           How it works: results are wrapped in a per-call random envelope (---BEGIN/END-UNTRUSTED-WEB-CONTENT-<token>---) with an explicit note telling the assistant to treat the enclosed text strictly as data and never obey instructions inside it; the random token means a malicious page cannot spoof the markers. The tool descriptions repeat the same warning.

        7. No cookies or credentials outbound
           What it is: a bare fetch client.
           How it works: the HTTP client carries no cookie jar and sends no authorization headers — only a plain identifying User-Agent — so nothing secret ever leaves the machine via a fetched page.

        8. Least-privilege tools
           What it is: the module exposes only web_search and web_fetch.
           How it works: disabled tools report themselves and refuse instead of running, and neither tool can read credentials, files, or other settings.

        Residual risk: framing mitigates but does not eliminate prompt injection — the final line of defense is the AI client itself. Keep domain rules deny-first for sensitive deployments.
        """;
}