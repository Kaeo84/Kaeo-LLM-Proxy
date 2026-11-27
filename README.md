# Kaeo LLM Proxy

A Windows system-tray application that acts as an Ollama API-compatible proxy, translating
requests from any Ollama client to one or more [llama.cpp](https://github.com/ggml-org/llama.cpp)
servers running their built-in OpenAI-compatible `/v1/` API.

## Why this exists

[Ollama](https://ollama.com/) clients (Open WebUI, Continue, etc.) expect the Ollama REST API.
[llama.cpp](https://github.com/ggml-org/llama.cpp) speaks a slightly different OpenAI-compatible
API. This proxy sits in between — you point your clients at `localhost:11434` and it routes each
request to the right llama.cpp instance, doing all format translation transparently.

## Features

- Translates the Ollama API to llama.cpp's OpenAI-compatible format
- Supports streaming (NDJSON) and non-streaming completions
- Copilot-compatible streaming — well-formed SSE (`data: [DONE]`, synthesized usage chunk, in-band
  errors) so `Microsoft.Extensions.AI` clients never hang awaiting a stream end
- Model name mapping — map any Ollama model name to the actual model loaded in llama.cpp
- Per-mapping upstream URL and timeout — route different models to different servers
- Request logging with [LiteDB](https://www.litedb.org/) (auto-archived by size, auto-expired by age)
- Application logging via [Serilog](https://serilog.net/) with rolling files
- System tray application — no console window, always available in the background
- Portable deployment — all data stored alongside the executable, easy to move or back up

## Supported Ollama Endpoints

| Incoming Ollama endpoint | Forwarded llama.cpp endpoint   |
|--------------------------|-------------------------------|
| `GET  /api/tags`         | `GET  /v1/models`             |
| `POST /api/show`         | `GET  /v1/models/{model}`     |
| `POST /api/generate`     | `POST /v1/completions`        |
| `POST /api/chat`         | `POST /v1/chat/completions`   |
| `POST /api/embeddings`   | `POST /v1/embeddings`         |

## OpenAI-Compatible Endpoints

The proxy also exposes an OpenAI-compatible `/v1/` API so clients such as Visual Studio Copilot, OpenAI SDKs, and other `/v1/` clients can use it directly.

> Streaming responses to these clients are made well-formed by the per-mapping
> [Copilot Compatibility](#copilot-compatibility) option (on by default).

| OpenAI endpoint          | Method | Purpose                                                                      |
|--------------------------|--------|------------------------------------------------------------------------------|
| `/v1/models`             | GET    | Lists enabled model mappings in OpenAI format, including `context_length`.   |
| `/v1/models/{model}`     | GET    | Returns a single enabled model mapping in OpenAI format.                     |
| `/v1/chat/completions`   | POST   | Forwards chat completion requests to the selected upstream llama.cpp server. |
| `/v1/completions`        | POST   | Forwards completion requests to the selected upstream llama.cpp server.      |
| `/v1/embeddings`         | POST   | Forwards embedding requests to the selected upstream llama.cpp server.       |

### The `/v1` prefix is optional

A client whose base URL is the bare proxy host — `http://localhost:11434` rather than
`http://localhost:11434/v1` — can call the same endpoints without the prefix. The proxy
normalises the path before forwarding, so either base-URL shape works and a client never has to
be re-pointed just to add `/v1`.

| Without the prefix               | Normalised to                         |
|----------------------------------|---------------------------------------|
| `POST /chat/completions`         | `POST /v1/chat/completions`           |
| `POST /completions`              | `POST /v1/completions`                |
| `POST /embeddings`               | `POST /v1/embeddings`                 |
| `POST /responses`                | `POST /v1/responses`                  |
| `POST /chat/completions/compact` | `POST /v1/chat/completions/compact`   |
| `POST /responses/compact`        | `POST /v1/responses/compact`          |
| `GET  /models`                   | `GET  /v1/models`                     |
| `GET  /models/{model}`           | `GET  /v1/models/{model}`             |
| `/responses/{id}`                | `/v1/responses/{id}`                  |

Any other path is still rejected with `404`, so the normalisation cannot silently redirect an
unrecognised endpoint onto the upstream.

## Portable Folder Structure

All configuration and data files live in a `Data` folder next to the executable for easy
backup and portability:

```
Kaeo LLM Proxy.exe
Data/
  settings.jsonc          # Configuration file
  logs/
	app/                  # Application logs (Serilog, rolling)
	requests/             # Request logs (LiteDB database files)
```

## Configuration

Edit `Data/settings.jsonc` to configure:

- **Listen address** — bind to localhost, `0.0.0.0` (all interfaces), or a specific IP
  - `localhost` (default) — only accessible from the local machine
  - `0.0.0.0` — accessible from the network (may require admin rights or a `netsh urlacl` entry)
  - A specific IP — binds to a particular network interface
- **Listen port** — default `11434` (the standard Ollama port)
- **Model name mappings** — each mapping specifies:
  - Ollama model name (how clients request it)
  - llama.cpp model name (the model name the upstream server knows)
  - Upstream URL (e.g. `http://192.168.1.10:8080`) — each mapping can point to a different server
  - Timeout in seconds (default: 300)
- **Logging preferences** — minimum log level, file size limits, retention period

### Network Access Note

To allow connections from other machines on your network:
1. Set `ListenAddress` to `"0.0.0.0"` in `settings.jsonc`
2. If running without administrator rights, add a URL ACL reservation:
   ```
   netsh http add urlacl url=http://+:11434/ user=DOMAIN\username
   ```
   Replace `DOMAIN\username` with your Windows account name.

## Web Dashboard Access

The web dashboard is **login-only by default**: every page and dashboard API call sits behind a
sign-in page. Dashboard accounts are configured per user in the **Users** tab (web or Windows app)
with a username, password, and role:

- **Admin** — full dashboard access (settings, mappings, users, modules, logs, usage, everything).
- **Read Only** — their own request logs (attributed by API key) and their own token usage.

The **Requests** view loads newest-first from the full logged history in the database (not just
the small in-memory window), then polls for new entries at the top. A **Requestor** column shows
who made each request — the local part of the user's email address (before `@`) — and is blank
when a request is unattributed.

Passwords are stored as hashes; signing in issues a protected browser session cookie. Disabling
a user, turning off their dashboard login, or changing their password revokes live sessions
immediately. Sign-in attempts are rate-limited. A disabled account (or one with dashboard login
turned off) cannot sign in even with the correct password; the sign-in page reports the specific
reason, and the Windows app's Users tab has an **Account enabled** checkbox to re-enable it.

**First run:** when no dashboard login exists yet, opening the dashboard from the local machine
shows a *Create administrator account* setup page that creates the first Admin login. Remote
machines cannot claim a fresh instance; create logins from the Windows app's Users tab instead.

Proxy **API keys never grant dashboard access** — they authenticate model and MCP requests only.

## API Key Grants (Models and MCP)

Each API key carries its own grants, edited in the **Users** tab (web: edit key; Windows app: the
key editor dialog) next to each other:

- **Allowed models** — one checkbox per configured model. A key with no boxes checked gets **no**
  model access, including model discovery, `/api/show`, chat, and passthrough.
- **Allow MCP server access** — a single on/off grant for the MCP server. A key without it gets
  `403 Forbidden` from the MCP endpoint, while a valid key without any grants still authenticates.

Dashboard role decides the defaults:

- **Admin** users always have every model and the full MCP server regardless of per-key grants.
- **Read Only** users get only what their key explicitly grants.

The MCP server accepts either the configured shared bearer credential (full access, unchanged) or a
proxy API key as the bearer token. Unknown keys get `401`; known keys without the MCP grant get
`403`. Grant changes apply immediately — the authentication cache is invalidated on every edit.

**Every MCP request now needs a bearer token.** Only the `/health` probe stays open, so an MCP
client that used to connect anonymously must add `Authorization: Bearer <key>` with a key that has
MCP access (or an Admin key), or keep using the shared credential if one is configured.

> **Upgrade note:** before per-key grants were enforced, an empty allowed-model list meant *all
> models*. It now means *no models*, so re-check existing Read Only keys after upgrading.

## API Key Recovery (Reveal and Regenerate)

Keys authenticate by hash, and the full value is also kept under reversible encryption so an
**Admin** can read it back. Each key row in the **Users** tab carries:

- **Reveal** (web) / **Reveal key** (Windows app) — shows the stored key, masked until you ask for
  it, with one-click copy.
- **Regen** (web) / **Regenerate key** (Windows app) — issues a new secret in place. The label,
  model grants, and MCP grant all survive; the previous key stops working immediately.

Read Only accounts cannot reveal or regenerate anything. Keys created before this feature have no
stored secret, so revealing one offers to regenerate it instead.

The encryption key is a random 32-byte file named `api-key-protection.key`, created next to the
application database on first use. It keeps a copied or synced database from leaking usable keys,
but it does not protect against someone who can read the whole data folder — protect that folder.
If the file is lost or replaced, stored keys can no longer be revealed; regenerate them.

Use HTTPS (or terminate TLS at a trusted proxy) before sharing the dashboard beyond localhost so
credentials and session cookies are not sent over untrusted HTTP networks.

**Disabling the login screen:** the *Require login* toggle (Windows app: Settings tab → Web
dashboard group; web: Settings → Web dashboard) turns the sign-in requirement off. It applies
immediately, and while it is off **anyone who can reach the dashboard has full Admin access** —
only use it on a trusted, isolated network.

## HTTPS Certificate and Web Server Control

HTTPS needs no hand-edited files: store the certificate as a **Credential** (Windows app:
**Credentials** tab; web: **Credentials** tab) and then pick it in **Settings → Web dashboard →
Certificate Credential (HTTPS only)** (web: Settings → *HTTPS certificate credential*).

- **PFX / PKCS#12** — put the base64 of the `.pfx` file in **Certificate** and the export password
  (if any) in **Secret**.
- **PEM** — put the certificate chain in **Certificate**, the private key in **Private Key**, and
  leave **Secret** empty.

The credential is read when the web server starts, so editing or replacing the credential and
restarting the web server picks up the new certificate. Selecting *(none)* serves plain HTTP.

Editing a credential in the web dashboard keeps the stored secret, private key, and certificate
when you leave those fields blank, so you can change a description without re-pasting the material.

### Starting and stopping the web server

- **Web dashboard** → Dashboard → **Web server** card: status, URL, listen address, certificate,
  and **Start** / **Stop** / **Restart**. Stop asks for confirmation first because it disconnects
  the page you are on.
- **Windows app** → Dashboard → **Web dashboard** group: the same status, URL, and three buttons.

Read Only dashboard users see the web server status but not the buttons. Stopping the web server
does not change the *Enabled* setting, so the next proxy start brings it back.

### Performance and web traffic

The Dashboard also shows the same **CPU**, **working set**, **GC gen2**, and **LOH** figures as the
Windows app, refreshed with the rest of the status card.

Web-server requests are logged to the same database as proxied traffic under a **Web** source:
**Traffic** → source **Web** (Admin only) in the dashboard, or the **Web** sub-tab of the Windows
app's **Logs** tab. Both list dashboard page loads, API calls, and rejected requests (401/403)
with the same retention and pruning as proxy logs. Static assets and the dashboard's own polling
endpoints (`/api/status`, `/api/stats`, `/api/logs`, ...) are skipped so the table stays readable,
but an error response is always recorded.

## Token Usage (Web Dashboard)

The web dashboard's **Usage** tab reports token consumption from the request-log database:

- **Read Only** users see their own usage only (matched by the API key that made each request).
- **Admin** users see everyone's usage and can scope the view to *All users*, themselves, or any
  single user, plus a **Usage by user** breakdown with per-user totals and share bars.

Pick a period (this/last month, this/last week, last 30/90 days), group the stacked chart by
**day / week / month**, and optionally filter to one model. The **By day** table expands each row
into per-model detail (requests, prompt, completion, total, cached prompt, and reasoning tokens).

Only requests that reported tokens are counted — zero-token probes are excluded. Requests made
without an API key (or before attribution existed) appear as **Unattributed** in the admin
by-user view. Days are grouped in the server's local time zone.

## Usage

1. Run `Kaeo LLM Proxy.exe` — it starts minimised to the system tray
2. Double-click the tray icon (or right-click → Open) to open the dashboard
3. Add model mappings with the name your clients will use and the upstream llama.cpp URL
4. Use **Fetch Models** to pull available model names from a running llama.cpp server
5. Point your Ollama-compatible client at `http://localhost:11434` (or your configured port)
6. The proxy routes each request to the correct llama.cpp instance and translates the response

Multiple instances are allowed by default (handy when the tray app and the headless host share a
machine). To enforce a single instance, turn off **Allow multiple instances** in the Settings tab
or the web dashboard's Settings → Proxy group; the change applies on the next launch. Give each
concurrently running instance its own listen ports.

## Context Compaction

Compaction is configured **per model mapping** — there is deliberately no global compaction
setting. A mapping that is left alone does nothing: requests are handed to the model untouched
and the model handles its own context.

### Manual Compaction

Both endpoints are always live and behave identically. Each forwards the conversation to a
model so that model produces the summary, and returns its response unchanged:

```
POST /v1/chat/completions/compact
POST /v1/responses/compact
```

Which model handles it depends on the mapping:

| Redirect manual compaction | Compaction Model | Result |
| --- | --- | --- |
| unchecked (default) | *(any)* | forwarded to the model named in the request |
| checked | *(None)* | forwarded to the model named in the request |
| checked | selected | forwarded to the selected compaction model |

The proxy never synthesizes a summary for these endpoints — a model always produces it. If a
redirect is configured but the target is missing, disabled, or has no upstream URL, the request
falls back to the model the client asked for and a warning is logged.

The same routing applies to a client's own `/compact` request sent as a normal chat request
(e.g. GitHub Copilot), which the proxy detects from its distinctive session-summary prompt.

### Automatic Compaction

When the estimated request size exceeds a mapping's compaction threshold, the proxy summarizes
the conversation with the mapping's compaction model and forwards the compacted request
upstream. Auto-compaction requires **all three** of:

1. **Auto-Compact Paths** — not `Disabled`
2. **A compaction threshold** — non-zero (`% of context` or absolute `tokens`; absolute wins)
3. **A Compaction Model** — selected, enabled, with an upstream URL

If any is missing the proxy does nothing and the request goes to the model untouched. A
successful compaction sets `X-Context-Compacted`, `X-Context-Original-Tokens` and
`X-Context-Compacted-Tokens` on the response.

`Auto-Compact Paths` accepts a single selection:

| Value | Paths auto-compacted |
| --- | --- |
| `Disabled` | none (default) |
| `Ollama /api/chat` | Ollama chat only |
| `OpenAI /v1/chat/completions` | OpenAI chat only |
| `Both` | the two above |
| `Proxy only (all paths)` | every path the proxy handles |

Reactive compaction — summarizing and retrying once after an upstream context-overflow error —
is governed by the same `Auto-Compact Paths` setting and the same compaction model requirement.

At most one compaction ever acts on a request: a request already routed for compaction is never
auto-compacted again.

### Configuration

Per mapping (Settings → Configure Model → *Compaction / Context thresholds*):

- **Auto-Compact Paths** — which paths may auto-compact (`Disabled` by default)
- **Compaction threshold (% of context)** and **Compaction threshold (tokens)** — `0` disables
- **Compaction Model** — the summarization target for both automatic and manual compaction
- **Redirect manual compaction** — send `/compact` requests to the compaction model

The dialog shows a live status line summarizing what the current selection will actually do.

### Copilot Compatibility

Per mapping (Settings → Configure Model → *Copilot Compatibility*), **on by default**. Clients
built on `Microsoft.Extensions.AI` — including Visual Studio Copilot — await a terminal stream
event and block indefinitely when one never arrives. Many OpenAI-compatible local servers do not
reliably emit one, and reject or ignore the `stream_options` block these clients send.

When enabled the proxy takes over both ends of that contract on the `/v1/*` passthrough and
manual-compaction paths:

- **Strips `stream_options`** (including `include_usage`) from the upstream-bound request, so a
  server that does not understand it is never asked to.
- **Guarantees the terminator** — if the upstream closes without `data: [DONE]`, the proxy appends
  it. A stream that already ended with `[DONE]` is left byte-for-byte untouched.
- **Synthesizes the usage chunk** the client asked for: when `include_usage` was requested and the
  upstream never reported usage, a terminal `chat.completion.chunk` carrying the captured token
  totals is emitted *before* `[DONE]`.
- **Reports late failures in-band** — once the SSE headers are on the wire the status code is fixed
  at `200`, so a failure is delivered as a `data: {"error":…}` frame followed by `data: [DONE]`
  rather than a silent connection close.

Disabling it relays the upstream's stream unchanged, with no added frames. This is independent of
the *Enable SSE keep-alive* option, which only holds the connection open while the upstream is still
processing the prompt.

## Visual Studio Extension

A [Visual Studio 2026 extension](Kaeo%20LLM%20Proxy%20VS%20Extension/README.md) provides a
GitHub Copilot-style chat panel that pairs with this proxy. It offers an agent/mode/model pill
bar, a streaming chat transcript, a multi-tab settings modal, and MCP tool integration — all
routed through the proxy's Ollama-compatible API.

- **Projects**: `Kaeo LLM Proxy VS Extension.Core` (host-agnostic client library) and
  `Kaeo LLM Proxy VS Extension` (VSIX).
- **Agent modes**: Interactive, Bypass, AutoPilot.
- **Built-in agents**: Agent, Ask, Plan (plus user-defined agents).
- **Multi-connection**: add any number of proxy connections; models are pulled live from each
  connection's `/api/tags` and tool-capable models (Ollama `tools` capability) are auto-enabled.

See the [extension README](Kaeo%20LLM%20Proxy%20VS%20Extension/README.md) for build, install,
and configuration details.

## System Requirements

- Windows 10 version 22000 (21H2) or later for the tray app
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Running headless on Linux / macOS

The tray app is Windows-only, but the engine, the web dashboard, and the MCP server are all
cross-platform. `Kaeo LLM Proxy Headless` runs the same `ProxyHostController` with no WinForms, so
on Linux or macOS you get the proxy plus the full web dashboard (everything is managed from the
browser, including model mappings and users).

Publish a self-contained build for the target platform — this can be cross-published from Windows,
so no .NET SDK is needed on the target machine:

```bash
# Apple Silicon
dotnet publish "src/Kaeo LLM Proxy Headless/Kaeo LLM Proxy Headless.csproj" \
  -c Release -r osx-arm64 --self-contained true -o artifacts/osx-arm64

# Linux x64
dotnet publish "src/Kaeo LLM Proxy Headless/Kaeo LLM Proxy Headless.csproj" \
  -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
```

Copy the output folder to the target machine, then run `./Kaeo LLM Proxy Headless`. The same
`Data/` folder layout is created next to the executable (`settings.jsonc`, the SQLite database,
and the rolling logs), so the folder stays portable exactly as it does on Windows.

On the first launch no dashboard account exists yet, so browse to the dashboard port
(`ListenPort + 1`, e.g. <http://localhost:11435>) and the login page offers first-run setup. Setup
is loopback-only by design; create additional accounts afterwards from the dashboard's **Users**
tab. `SIGINT`/`SIGTERM` drains in-flight requests and shuts down cleanly.

The Windows-only pieces (the tray app and the Visual Studio extension) are not part of the headless
host. Module packages (`.kaeomod`) are per-platform; build them on the target platform with
`./Kaeo LLM Proxy Headless --module-pack <module-dir>` (add `--module-sign --key <private-key.pem>`
to sign), or leave them out and run with the built-in features.

## GitHub Pages

A project landing page lives in the [`docs/`](docs/) folder and is published at
**<https://kaeo84.github.io/Kaeo-LLM-Proxy/>**.

To enable Pages for a fork or fresh clone:

1. Go to **Settings → Pages** in your repository.
2. Under **Build and deployment**, choose **Deploy from a branch**.
3. Select your default branch and the **`/docs`** folder.
4. Click **Save** and wait up to 10 minutes for the site to publish.

## License

This project is **free for personal, educational, and research use**.  
See [LICENSE](LICENSE) for the full terms.

**Restrictions:**
- ❌ Commercial use requires a separate license
- ❌ Government use is prohibited
- ❌ No derivative works for commercial gain
- ✅ Attribution to the original creator is required

For commercial licensing inquiries, please contact the repository owner.
