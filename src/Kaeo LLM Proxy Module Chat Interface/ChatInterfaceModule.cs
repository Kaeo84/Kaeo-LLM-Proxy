using Kaeo.LlmProxy.Core.Modules;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// The Chat Interface module entry point discovered by the host via <see cref="IKaeoModule"/>.
/// Contributes the chat_send/chat_ask/chat_read/chat_channels tools to the host's built-in MCP
/// server so the AI model can message the user on external chat platforms (Discord, MS Teams,
/// Slack, Google Chat, Zoom) and receive the user's replies back. Channels and feature settings
/// persist in the shared application database; a background poll loop captures replies.
/// </summary>
public sealed partial class ChatInterfaceModule : IKaeoModule, IMcpToolModule, IRunnableModule
{
    public const string Version = "1.0.0";

    private ModuleContext? _context;
    private ChatRepository? _repository;
    private ChatBridge? _bridge;
    private ChatActivityLogger? _activity;

    public string Id => "kaeo.chatinterface";

    public string Name => "Chat Interface";

    string IKaeoModule.Version => Version;

    public string Description =>
        "Chat tools (chat_send/chat_ask/chat_read/chat_channels) for the built-in MCP server: " +
        "the model can message the user on Discord, MS Teams, Slack, Google Chat, or Zoom and " +
        "receive the user's replies.";

    internal ChatRepository Repository =>
        _repository ?? throw new InvalidOperationException("Module not initialized.");

    internal ChatBridge Bridge =>
        _bridge ?? throw new InvalidOperationException("Module not initialized.");

    internal ChatActivityLogger Activity =>
        _activity ?? throw new InvalidOperationException("Module not initialized.");

    internal ISecretProvider Secrets =>
        _context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");

    public void Initialize(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        try
        {
            ApplySchema(context.Database);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // Another app instance may hold the database; degrade gracefully. The schema is
            // idempotent and will be applied by whichever instance can write it.
            Log.Warning(ex, "Chat Interface module could not apply its database schema");
        }

        _repository = new ChatRepository(context.Database);
        _activity = new ChatActivityLogger(context.ActivityLog, () => _repository.LoadSettings().McpLogLevel);
        _bridge = new ChatBridge(_repository, context.Secrets, _activity);
    }

    /// <summary>Tool targets for the host's MCP server; the session info carries the client address.</summary>
    public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) =>
        [new ChatTools(Bridge, Repository, session, Activity)];

    // ── IRunnableModule ─────────────────────────────────────────────────────
    // "Running" means the reply poll loop is active; chat_send works either way. The host
    // stops the module on disable/remove/shutdown, which cancels every pending chat_ask.

    public bool IsRunning => _bridge?.IsRunning == true;

    public event EventHandler<string>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge is null)
            throw new InvalidOperationException("Module not initialized.");

        if (_bridge.IsRunning)
            return Task.CompletedTask;

        _bridge.Start();
        StatusChanged?.Invoke(this, "Running");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_bridge is null || !_bridge.IsRunning)
            return;

        await _bridge.StopAsync().ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Stopped");
    }

    internal const string HelpText =
        """
        CHAT INTERFACE MODULE

        This module lets the AI model talk to you on external chat platforms. It adds tools
        to the built-in MCP server for sending messages to a configured channel and receiving
        your replies back, so a long-running task can notify you, ask a question, and continue
        with your answer instead of stalling at the desktop.

        TOOLS
        - chat_send: sends a message/notification to a channel. Fire-and-forget.
        - chat_ask: sends a question and BLOCKS until you reply or the timeout elapses
          (default 300 seconds, configurable 10..3600). Returns your reply, framed as
          untrusted content. Requires a channel with receive support and polling running.
        - chat_read: reads recent messages captured on a channel (while polling runs).
        - chat_channels: lists configured channels, whether they can receive replies, and
          their poll health.

        Polling must be running (Start button on this module's settings page, or the host's
        module start) for replies to be captured. Sending works even when polling is stopped.

        PLATFORM SETUP

        DISCORD
        1. Create an application and bot at https://discord.com/developers/applications.
        2. Copy the bot token into a host credential (Secret field).
        3. Invite the bot to your server with the Send Messages and Read Message History
           permissions (OAuth2 URL generator, scope "bot").
        4. Enable Developer Mode in Discord settings, right-click the target channel, and
           copy its channel id into the channel's Target field.
        Sending and receiving both use the bot token.

        MICROSOFT TEAMS
        Sending: add an Incoming Webhook via the Workflows app in the target channel/chat and
        paste its URL into the channel's Webhook URL field. No credential is needed to send.
        Receiving (optional): register an app in Entra ID (Azure AD), grant Microsoft Graph
        application permission ChatMessage.Read.Chat (admin approval may be required), create
        a client secret, and store it in a host credential. Fill in the tenant id, client id,
        and chat id fields on the channel. Without these the channel is send-only.

        SLACK
        1. Create an app at https://api.slack.com/apps with bot scopes chat:write and
           channels:history (add groups:history/im:history for private channels/DMs).
        2. Install it to the workspace and store the bot token (xoxb-...) in a host credential.
        3. Invite the bot to the channel (/invite @bot) and copy the channel id (shown at the
           bottom of "View channel details") into the Target field.

        GOOGLE CHAT
        Sending: in the target space, open Apps & integrations > Webhooks, create an incoming
        webhook, and paste its URL into the Webhook URL field.
        Receiving (optional): create a service account in Google Cloud, enable the Google
        Chat API, download the service-account JSON key, and store the FULL JSON text as the
        Secret of a host credential. Add the service account (or its app) to the space. The
        Target field is the space path, e.g. spaces/AAAA.

        ZOOM
        1. Create a Server-to-Server OAuth app at https://marketplace.zoom.us.
        2. Add the chat scopes (imchat:bot or chat_message:read/write as offered) and activate
           the app; add it to the target channel.
        3. Store the client id as the Username and the client secret as the Secret of a host
           credential; put the Account ID into the channel's Account id field.
        4. Copy the channel id from the Zoom web portal into the Target field.

        SECURITY NOTES
        - Replies are UNTRUSTED CONTENT: every incoming message is wrapped in a random-token
          envelope telling the model to treat it as data and never follow instructions in it.
        - Webhook URLs contain secret tokens, and bot tokens/client secrets are secrets: they
          are stored in the host's credential store or the channel's webhook field, never
          written to logs (URLs are redacted in log output).
        - chat_ask blocks the tool call until a reply arrives or the timeout elapses; the
          first human message after the question (or an explicit reply to it) is taken as
          the answer, so avoid unrelated chatter on the channel while a question is pending.
        - Message text returned to the model is capped by the Max message chars setting.

        MCP LOGGING
        Chat activity can be recorded into the host's MCP request log (Logs tab, MCP sub-tab).
        "Errors only" records failures and timeouts; "Connectivity & errors" (the default)
        also records sends, replies, and poll failures; "Full (verbose)" additionally records
        every tool call with message text. Changes apply within a few seconds.
        """;

    /// <summary>
    /// Baseline schema for the module's tables, applied during initialization. Idempotent:
    /// safe to run on every startup.
    /// </summary>
    private const string SchemaScript = """
-- Kaeo LLM Proxy Module Chat Interface baseline schema.
-- Idempotent: safe to run on every startup.

-- Named chat channels the AI client can address by name.
-- provider_type: discord | teams | slack | googlechat | zoom.
-- credential_name references the host's central credential store (bot token / client secret /
-- service-account JSON). webhook_url holds the incoming-webhook URL for webhook-based sends.
-- config_json holds provider-specific non-secret configuration (Teams tenantId/clientId/chatId,
-- Zoom accountId).
CREATE TABLE IF NOT EXISTS mcp_chat_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    provider_type TEXT NOT NULL,
    target TEXT NOT NULL DEFAULT '',
    credential_name TEXT NULL,
    webhook_url TEXT NULL,
    config_json TEXT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    poll_interval_seconds INTEGER NOT NULL DEFAULT 15
);

-- Key/value settings for the Chat Interface feature (tool toggles, ask timeout, message cap,
-- default poll interval, MCP log level).
CREATE TABLE IF NOT EXISTS mcp_chat_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
""";

    private static void ApplySchema(IModuleDatabase database) => database.ExecuteSchemaScript(SchemaScript);
}