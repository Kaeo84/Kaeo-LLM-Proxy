# Kaeo LLM Proxy

A cross-platform LLM proxy with a built-in web dashboard. It speaks the **Ollama**, **OpenAI**, and
**Anthropic** APIs on the front door and forwards each request to whatever your backends actually
speak — llama.cpp or any other OpenAI-compatible server, Ollama, or the Anthropic Messages API —
translating requests, responses, and streams in both directions. Around that engine it adds user
administration, API keys, a credential store, per-user model and tool grants, prompt injection,
and full logging, reporting, and statistics.

## Platform support

| Platform | What you get |
|----------|--------------|
| **Windows** | Full engine, web dashboard, MCP server, system-tray app, and Visual Studio extension |
| **macOS** (Apple Silicon, Intel) | Full engine, web dashboard, and MCP server via the headless host |
| **Linux** (x64, ARM64) | Full engine, web dashboard, and MCP server via the headless host |

**The web GUI is the cross-platform GUI.** The engine — proxy, MCP server, dashboard, users, keys,
logging, statistics — is identical on Windows, macOS, and Linux, and so is the dashboard served from
it. Only the two desktop front ends are Windows-only: the system-tray application and the Visual
Studio extension. On macOS and Linux the headless host runs the same engine and the same dashboard,
so there is nothing to give up but the tray icon (see
[Running headless on Linux / macOS](#running-headless-on-linux--macos)).

## Why this exists

It started as a bridge: [Ollama](https://ollama.com/) clients (Open WebUI, Continue, etc.) expect
the Ollama REST API while [llama.cpp](https://github.com/ggml-org/llama.cpp) speaks a slightly
different OpenAI-compatible API, so this proxy sat in between and translated. The same idea turned
out to be useful for everything else — ChatGPT-style clients, Anthropic clients, several backends at
once, per-user access control, and one place to see what every client is actually doing — so the
translation core grew a management plane around it. You still point clients at `localhost:11434`;
what sits behind that port is now configurable, multi-protocol, and auditable.

## Features

**Protocol translation**

- One listener speaks **Ollama** (`/api/*`), **OpenAI/ChatGPT-style** (`/v1/*`), and **Anthropic**
  (`/v1/messages`) — see [API Translation](#api-translation-anthropic-openai-ollama)
- Every model mapping chooses its own upstream protocol: any OpenAI-compatible server (llama.cpp,
  vLLM, LM Studio, OpenRouter, OpenAI), a real Ollama server, or the Anthropic Messages API
- Bidirectional translation of requests, responses, and streams, including tool/function calls,
  images, system prompts, stop sequences, sampling parameters, and token usage
- Streaming (NDJSON for Ollama, SSE for OpenAI and Anthropic) and non-streaming, for chat, text
  completions, embeddings, and responses
- Copilot-compatible streaming — well-formed SSE (`data: [DONE]`, synthesized usage chunk, in-band
  errors) so `Microsoft.Extensions.AI` clients never hang awaiting a stream end
- Anthropic `max_tokens` and `anthropic-version` defaults supplied when the client omits them

**Models and routing**

- Model name mapping — map any client model name to the model actually loaded on the backend
- Per-mapping upstream URL and timeout — route different models to different servers
- Per-model enable/disable, display name, and context length for `/v1/models`
- Auto-managed models — one mapping per upstream is enough; models the upstream reports register
  themselves, and models it stops reporting disable themselves after a grace period
- Context compaction — manual `/compact` endpoints plus automatic, threshold-driven summarisation

**Access security**

- User administration with name, email, notes, and multiple roles per user
- Per-user API keys — create, delete, regenerate, and (Admin only) reveal; masked by default
- Per-user model grants and per-user MCP tool grants; Admin always gets everything
- Dashboard login with optional HTTPS served from a certificate in the credential store

**Operations**

- Cross-platform web dashboard — Windows, macOS, and Linux serve the identical GUI
- Start, stop, and restart for the proxy, the MCP server, and the web server, from either GUI
- Credential store for central model management — one named, encrypted secret reused by mappings,
  modules, and the HTTPS listener
- Custom prompt injection — named instruction sets applied per mapping, plus a compaction set
- Request logging to SQLite via [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)
  (auto-expired by age, configurable or forever), with redaction of sensitive fields
- Application logging via [Serilog](https://serilog.net/) with rolling files
- Live statistics — request/error/token totals, requests per second, CPU and memory, per-model
  connection health, token-usage reporting, and web-traffic accounting
- Windows system-tray application — no console window, always available in the background
- Headless host for Windows services, macOS, and Linux
- Portable deployment — all data stored alongside the executable, easy to move or back up

## API Translation (Anthropic, OpenAI, Ollama)

The proxy is not tied to one protocol on either side. Clients may speak **any** of the three
supported protocols and each model mapping may point at **any** of the three, so one listener can
serve an Ollama client, a ChatGPT-style client, and an Anthropic client at the same time while
fronting a mixed fleet of backends.

### Inbound — what clients call

| Client protocol | Endpoints | Details |
|-----------------|-----------|---------|
| **Ollama** | `GET /api/tags`, `GET /api/ps`, `GET /api/version`, `POST /api/show`, `POST /api/chat`, `POST /api/generate`, `POST /api/embeddings`, `POST /api/embed` | [Supported Ollama Endpoints](#supported-ollama-endpoints) |
| **OpenAI / ChatGPT-style** | `GET /v1/models`, `GET /v1/models/{model}`, `POST /v1/chat/completions`, `POST /v1/completions`, `POST /v1/embeddings`, `POST /v1/responses`, other `/v1/*` passthrough | [OpenAI-Compatible Endpoints](#openai-compatible-endpoints) |
| **Anthropic** | `POST /v1/messages`, `POST /messages` | [Anthropic Messages Endpoint](#anthropic-messages-endpoint) |

### Upstream — what a mapping points at

Set `UpstreamType` on the mapping:

| `UpstreamType` | Display name | Upstream API | Typical target |
|----------------|--------------|--------------|----------------|
| `OpenAI` | OpenAI Compatible | `POST /v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `GET /v1/models` | llama.cpp, vLLM, LM Studio, OpenRouter, OpenAI |
| `Ollama` | Ollama | `POST /api/chat`, `/api/generate`, `/api/embeddings`, `GET /api/tags` | a real Ollama server |
| `Anthropic` | Anthropic | `POST /v1/messages` | `https://api.anthropic.com`, or a gateway in front of it |

`LlamaCpp` is still accepted as a legacy alias for `OpenAI`, so older settings files keep working.

### Translation is per mapping

`EnableApiTranslation` (default `true`) on each mapping switches bidirectional translation on:

- An Ollama or Anthropic request is rewritten into the upstream's native shape, and the upstream's
  answer — streaming or not — is rewritten back into the shape the client asked for. That covers
  tool/function calls (`tool_calls` ↔ `tool_use`/`tool_result`), base64 images, system prompts,
  stop sequences, sampling parameters, finish reasons, and token usage.
- With translation **off** the request is forwarded verbatim, which is what you want when client and
  upstream already speak the same protocol and you only need routing and logging.
- Anything the target protocol cannot express is dropped rather than sent as an invalid field, so a
  translation mismatch degrades instead of failing the request.

**Anthropic-specific defaults.** Anthropic requires `max_tokens`, so a mapping to an Anthropic
upstream supplies `AnthropicMaxTokens` (default `4096`) when the client omits it, and sends
`anthropic-version: 2023-06-01` (configurable via `AnthropicApiVersion`) on every call.

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
| `/v1/chat/completions`   | POST   | Forwards chat completion requests to the selected upstream.                  |
| `/v1/completions`        | POST   | Forwards completion requests to the selected upstream.                       |
| `/v1/embeddings`         | POST   | Forwards embedding requests to the selected upstream.                        |
| `/v1/responses`          | POST   | Forwards Responses-API requests to the selected upstream.                    |
| `/v1/*` (anything else)  | any    | Transparent passthrough to the upstream's own `/v1/` surface.                |

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

## Anthropic Messages Endpoint

The proxy accepts the Anthropic Messages API, so an Anthropic SDK client — or Claude Code, or any
tool that speaks `/v1/messages` — can be pointed at it and reach a model served by an
OpenAI-compatible or Ollama backend. The translation is complete in both directions, including
streaming, `tool_use`/`tool_result` blocks, images, system prompts, and usage counters.

| Endpoint                | Method | Purpose                                                                          |
|-------------------------|--------|----------------------------------------------------------------------------------|
| `/v1/messages`          | POST   | Accepts an Anthropic request, translates it to the mapping's upstream, and translates the answer (streaming or not) back into Anthropic events. |
| `/messages`             | POST   | The same endpoint without the `/v1` prefix.                                      |

Point an Anthropic client at the proxy with a base URL of `http://localhost:11434` and use any
mapped model name. Set the mapping's `UpstreamType` to `Anthropic` to forward to
`https://api.anthropic.com` instead — the request then passes through as-is, with
`AnthropicMaxTokens` and `AnthropicApiVersion` filling in anything the client left out.

## Portable Folder Structure

All configuration and data files live in a `Data` folder next to the executable for easy
backup and portability:

```
Kaeo LLM Proxy.exe
Data/
  settings.jsonc          # Configuration file (settings.jsonc.bak is the previous version)
  Database/
	kaeo_llm_proxy.db     # Request log, users, API keys, mappings (SQLite)
  logs/
	app/                  # Application logs (Serilog, rolling)
	startup-*.log         # Startup trace written before Serilog is configured
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

Everything in `settings.jsonc` has a GUI: the Windows app's **Settings** tab and the web
dashboard's **Settings**, **Models**, **Credentials**, and **Users** tabs edit the same file and the
same SQLite database, so the two front ends always agree.

### Network Access Note

To allow connections from other machines on your network:
1. Set `ListenAddress` to `"0.0.0.0"` in `settings.jsonc`
2. If running without administrator rights, add a URL ACL reservation:
   ```
   netsh http add urlacl url=http://+:11434/ user=DOMAIN\username
   ```
   Replace `DOMAIN\username` with your Windows account name.

### Wildcard addresses and the browser

`0.0.0.0` (and `::`, `*`, `+`) is an instruction to *listen on every interface*, not a destination
you can browse to. Whenever the app has to hand you a URL — the tray menu's **Open Web Dashboard**,
the Windows Dashboard tab, or the headless host's startup line — a wildcard or loopback bind is
reported as `http://localhost:<port>`, and any other bind is reported as-is (IPv6 literals are
bracketed so the URL stays valid). The listener itself still binds the wildcard, so remote clients
keep working; only the displayed URL is normalised.

## Credential Store (Central Model Management)

API keys, bearer tokens, usernames/passwords, SSH private keys, and HTTPS certificates are stored
**once**, in the credential store, and referenced **by name** from wherever they are needed. That
keeps a fleet of mappings on one rotating secret instead of N copies of it.

- **One named entry, many consumers.** A credential is referenced by `CredentialName` from model
  mappings, from module providers (Web Search, Code Vector Store, Chat Interface), and from the
  HTTPS listener (`WebDashboard.CertificateCredentialName`). Rotate the secret in one place and
  every consumer picks it up.
- **Encrypted at rest.** Secrets are encrypted with AES-256-GCM using a key derived from your
  **security passphrase** via PBKDF2-SHA256 (600,000 iterations). The plaintext never lands in
  `settings.jsonc`, and the passphrase itself is never stored — only its SHA-256 hash, which is
  DPAPI-protected on Windows. On macOS and Linux the hash is stored without DPAPI, so protect the
  `Data` folder with filesystem permissions and use a passphrase you do not reuse elsewhere.
- **Write-only after save.** Once stored, a secret is never sent back to a GUI in clear text — the
  dashboard shows only a masked value and whether it is set. Leave the field blank while editing to
  keep the existing secret, or supply a new value to replace it.
- **Certificate credentials** carry a PEM/PFX certificate and private key, which is how HTTPS for
  the dashboard, proxy, and MCP endpoint is configured — see
  [HTTPS Certificate and Web Server Control](#https-certificate-and-web-server-control).
- **Managed in both GUIs.** The **Credentials** tab (web) and the Windows **Credentials** tab list,
  add, edit, and delete entries; the name is the contract, so a rename is a breaking change for
  every consumer and the GUI warns before doing it.

## Auto-Managed Models (Upstream Discovery)

A machine that can run several models does not need several hand-written mappings. Tick
**Auto-managed by upstream** on one mapping per upstream server, and the proxy keeps the rest of
that server's models in step with what the server actually reports from its `/v1/models` endpoint:

- **Register** — a model the upstream lists but the proxy does not know about is added as a clone of
  the auto-managed mapping (same upstream URL, credential, instruction set, and sampling defaults).
  Compaction targets are deliberately *not* inherited, so a new model never points at the template's
  summarizer.
- **Disable after a grace period** — a model the upstream stops listing is marked missing on the
  first scan that misses it, and only switched off once it has been missing for
  **`ModelAutoDiscoveryOfflineMinutes`** (default 10). Disabling is the same switch as the manual
  one: the model disappears from `/v1/models` and `/api/tags` and stops routing.
- **Re-activate** — a model that comes back is switched on again automatically, but only when
  discovery was the thing that switched it off. An operator who disables a model by hand is never
  overridden.
- **A failed read changes nothing** — if the upstream is down, times out, or rejects the call, every
  mapping is left exactly as it was. An outage of a whole server never looks like "all the models
  were deleted".
- **Proxy names stay unique** — a model id already used as a proxy name by any mapping (hand-written
  or from another upstream) is skipped rather than overwritten.

Two settings control it, in the **Models** tab (web) or the **Heartbeats** group (Windows app):

| Setting | Default | Range | Meaning |
|---------|---------|-------|---------|
| `EnableModelAutoDiscovery` | `true` | on/off | Master switch. Off leaves every auto-managed mapping exactly where it is. |
| `ModelAutoDiscoveryIntervalSeconds` | `60` | 15–3600 | How often each upstream is polled. |
| `ModelAutoDiscoveryOfflineMinutes` | `10` | 1–1440 | How long a model must stay missing before it is disabled. |

Both GUIs show the last scan and its outcome, and both offer **Discover now** (web: *Discover now*,
Windows: *Discover models now*) to run one scan immediately instead of waiting for the next tick.
The scan interval and master switch are re-read every tick, so changing them needs no restart.

### Bootstrapping a new machine

A model cannot register itself from nothing: discovery polls the upstreams that *auto-managed
mappings* already point at, so the first mapping on a new server has to be created once. After that,
every other model on that server registers, retires, and comes back on its own — the seed only has
to name one model the server really serves.

Tick **Auto-managed by upstream** on any mapping in the Models tab, or create the seed over the
dashboard API. `scripts/register-auto-managed-models.sh` does it from a shell (macOS, Linux, WSL):

```sh
KAEO_API_KEY='kaeo-…' \
KAEO_UPSTREAM='http://127.0.0.1:11434' \
./scripts/register-auto-managed-models.sh
```

It reads the first model id from the upstream's `/v1/models` (or takes `KAEO_SEED_MODEL`), creates
one mapping named `kaeo-auto` with `autoManaged: true`, and runs a scan immediately. Re-running it
is safe — the seed is skipped when it already exists. Other overrides: `KAEO_DASHBOARD` (default
`http://127.0.0.1:11435`), `KAEO_UPSTREAM_TYPE` (`1` OpenAI-compatible, `2` Ollama, `3` Anthropic),
`KAEO_CREDENTIAL` (a stored credential for the upstream), `KAEO_SEED_NAME`, and `KAEO_SEED_HIDDEN`.

`KAEO_API_KEY` is a service account's key and needs no password anywhere — see
[Service Accounts and the Registration API](#service-accounts-and-the-registration-api). Without
it the script falls back to a dashboard login:

```sh
KAEO_PASSWORD='your-dashboard-password' KAEO_USER='admin' \
./scripts/register-auto-managed-models.sh
```

The same thing with plain `curl`, authenticated by API key:

```sh
# 1. One auto-managed seed mapping. OllamaName is the proxy name and LlamaCppName is
#    the upstream model name — historical wire names that predate the JSON contract.
curl -sS -H "Authorization: Bearer $KAEO_API_KEY" -H 'Content-Type: application/json' \
  -d '{"OllamaName":"kaeo-auto","LlamaCppName":"llama3.2","upstreamUrl":"http://127.0.0.1:11434","upstreamType":2,"autoManaged":true,"isEnabled":true}' \
  http://localhost:11435/api/mappings

# 2. Scan now instead of waiting for the next tick, then read the outcome.
curl -sS -H "Authorization: Bearer $KAEO_API_KEY" -X POST http://localhost:11435/api/mappings/discover
curl -sS -H "Authorization: Bearer $KAEO_API_KEY" http://localhost:11435/api/mappings/discovery
```

Swap the header for `-b cookies.txt` (after `/api/auth/login`) to do the same with a dashboard
session instead. Creating or changing mappings needs **Registration Read-Write** for an API key, or
**Admin** / **Model Admin** for a dashboard login. The dashboard listens on the proxy port + 1 (11435
by default) and requires sign-in unless that is switched off. On a machine that also runs the proxy,
move the proxy's `ListenPort` off 11434 so it does not collide with Ollama.

## Service Accounts and the Registration API

A machine that registers models — a startup script on a model server, a CI job, a scheduled task —
should not hold a dashboard password. Give it a **service account** instead:

1. **Users** tab → **Add user**. A name is enough; leave **Enable dashboard login** off, since the
   account exists only for its key.
2. Give it **Registration Read-Write** (or **Registration Read Only** if it only reads).
3. Open the card's ⋮ menu → **Create API key**, and copy the key.

That key authenticates `/api/mappings` and nothing else:

| Call | Needs |
|------|-------|
| `GET /api/mappings`, `GET /api/mappings/discovery` | Registration Read Only |
| `POST /api/mappings`, `PUT /api/mappings/{id}`, `DELETE /api/mappings/{id}`, `POST /api/mappings/discover` | Registration Read-Write |

Send it as `Authorization: Bearer <key>` (or `x-api-key: <key>` — the same two forms the proxy
accepts for model traffic). Everything outside `/api/mappings` still answers `401` for a key: users,
keys, settings, credentials, modules, logs, usage, and the dashboard tabs are reachable only with a
dashboard session, so a leaked service-account key cannot pivot into administration. That holds even
when the key belongs to an **Admin** user.

Revoking is immediate: delete the key, or disable the user, and the next call fails. Dashboard-login
settings do not apply to a key, so a service account never needs a password, and turning a login off
cannot lock a key out. Registration roles grant no dashboard tabs at all — a service account that
does sign in sees an empty dashboard, which is intentional.

## Custom Prompt Injection (Instruction Sets)

Named **instruction sets** inject your own system prompt into every request that a mapping serves,
without editing each client. Define them once and attach them per model:

- An instruction set is a name, a description, and the instruction text.
- A mapping selects one with `InstructionSetName`; mappings that name none are untouched.
- The text is applied by `SystemPromptComposer`, which merges the instruction text with any
  leading `system` messages the client already sent into **exactly one** system message, separated
  by blank lines. Strict chat templates — Qwen3, for example — reject a second system message, so
  folding rather than prepending is what keeps those models working.
- A separate **compaction** instruction set (`CompactionInstructionSetName`) controls the prompt
  used when the proxy summarises a conversation, so you can tune summary style independently of
  the answering prompt.
- Editable on the **Models** tab next to the mapping it applies to.

## Web Dashboard Access

The web dashboard is **login-only by default**: every page and dashboard API call sits behind a
sign-in page. Dashboard accounts are configured per user in the **Users** tab (web or Windows app)
with a username, password, and one or more roles. Roles combine (the permissions are the union), so
a user can be, say, *User Admin* plus *Dashboard Read Only*:

- **Admin** — everything: every model, every MCP tool, every tab, and every action.
- **Read Only** — their own request logs (attributed by API key) and their own token usage.
- **Models Read Only** — Read Only plus the Models tab in read-only form, listing every mapping.
- **Own Models Read Only** — Read Only plus the Models tab in read-only form, limited to the
  mappings granted to that user.
- **Dashboard Read Only** — the Dashboard tab: proxy/MCP/web-server state, performance, and
  traffic totals, without the control buttons.
- **Dashboard Read-Write** — Dashboard Read Only plus start/stop/restart of the proxy, the MCP
  server, and the web dashboard.
- **User Admin** — Read Only plus adding, updating, and removing users, their roles, their API
  keys, and their access grants.
- **Model Admin** — Read Only plus adding, updating, and removing model mappings.
- **Test Console** — the **Test Console** tab, limited to the models that user can access.
- **Registration Read Only** — the model registration API in read-only form, for a service
  account's API key. Grants no dashboard tabs.
- **Registration Read-Write** — Registration Read Only plus registering, updating, and removing
  models and running a discovery scan. Grants no dashboard tabs.

Admin always holds every permission, and a user with no valid role has no dashboard access at all
rather than falling back to Read Only. The **Sys Log** tab, the live stream panes, and the
**Settings**, **Credentials**, and **Modules** surfaces stay Admin-only because they expose payload
text and other users' traffic.

The **Help** tab is always present and is filtered by your roles, so it documents only the surfaces
you can actually reach: it opens with a *Your access* card listing your roles and the capabilities
they grant, then shows one topic per tab, control, and concept your roles unlock. The role names
shown in the dashboard and in Help come from the server, so they cannot drift from the roles the
API enforces.

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

Proxy **API keys are not a dashboard login.** They authenticate model and MCP requests, plus the
model registration API described in
[Service Accounts and the Registration API](#service-accounts-and-the-registration-api) — and
nothing else. A key cannot open a dashboard tab, read another user's logs, or change settings, even
when it belongs to an Admin user.

## User Grants (Models and MCP)

Model and MCP access belong to the **user**, not to an individual API key, so every key a user
holds has the same reach. Both grants are edited in the **Users** tab (web: *Edit user*; Windows
app: the user editor dialog) next to each other:

- **Allowed models** — one checkbox per configured model. A user with no boxes checked gets **no**
  model access, including model discovery, `/api/show`, chat, and passthrough.
- **Allow MCP server access** — the on/off grant for the MCP server. A user without it gets
  `403 Forbidden` from the MCP endpoint, while a valid key without any grants still authenticates.
- **MCP modules** — with MCP access on, one checkbox per module. **Leave every box clear to allow
  every module**; ticking any narrows the user to exactly those modules. A tool from an unticked
  module is not listed by `tools/list` and a `tools/call` for it is refused, so the limit holds even
  if a client already knows the tool's name. Note the deliberate asymmetry with models: an empty
  model list means *no models*, an empty module list means *every module*.

Roles decide the defaults:

- **Admin** users always have every model and the full MCP server regardless of their grants.
- Everyone else gets only what their user record explicitly grants.

> **Upgrade note:** grants used to be stored per API key. On first start the app folds every key's
grants into the owning user and clears the per-key copy, so nothing is lost — but a user who owns
several keys now gets the union of what those keys could reach. Re-check Read Only users after
upgrading, and remember that an empty allowed-model list means *no models*.

The MCP server accepts either the configured shared bearer credential (full access, unchanged) or a
proxy API key as the bearer token. Unknown keys get `401`; known keys without the MCP grant get
`403`. Grant changes apply immediately — the authentication cache is invalidated on every edit.

**Every MCP request now needs a bearer token.** Only the `/health` probe stays open, so an MCP
client that used to connect anonymously must add `Authorization: Bearer <key>` with a key that has
MCP access (or an Admin key), or keep using the shared credential if one is configured.

> **Upgrade note:** before grants were enforced, an empty allowed-model list meant *all* models. It
> now means *no models*, so re-check existing users after upgrading.

## MCP Server and Modules

The app is also an **MCP (Model Context Protocol) server**, so an MCP-capable client — Visual Studio
Copilot agent mode, Claude Desktop, or anything else that speaks MCP — can call tools that the proxy
hosts. It listens on its own port, has its own start/stop/restart controls in both GUIs, and logs
its traffic under the **MCP** source so tool calls are visible next to chat traffic.

Authentication is a bearer token: either the configured shared credential or any proxy API key whose
user holds the MCP grant (see [User Grants](#user-grants-models-and-mcp)). Only `/health` is open.
Per-module grants narrow which tools that key can see and call.

Built-in and module-provided tools:

| Module | Tools | What it does |
|--------|-------|--------------|
| **Web Search** | `web_search`, `web_fetch` | Search the web and fetch a page as clean text for the model to read. |
| **SSH** | `ssh_connect`, `ssh_exec`, `ssh_disconnect`, `ssh_list` | Run commands on remote hosts over SSH, with connection reuse. |
| **Code Vector Store** | `code_search`, `code_index`, `code_sync_repo`, `code_list_collections`, `code_status`, `code_remove`, `code_reindex` | Index a codebase into embeddings and answer "where is X handled?" with real file/line evidence. Supports remote HTTP embedding endpoints and a local ONNX CPU backend, agent-pushed indexing, and server-side git mirrors. |
| **Chat Interface** | `chat_send`, `chat_ask`, `chat_read`, `chat_channels` | Bridge the model to Discord, Microsoft Teams, Slack, Google Chat, and Zoom channels. |

Modules are packaged as `.kaeomod` files (signed when you want tamper detection) and managed on the
**Modules** tab. They are per-platform when they carry native dependencies — see
[docs/Module-Packaging.md](docs/Module-Packaging.md) and the `--module-pack` / `--module-sign`
verbs of the headless host.

## API Key Recovery (Reveal and Regenerate)

Keys authenticate by hash, and the full value is also kept under reversible encryption so an
**Admin** can read it back. Each user card and each key row in the **Users** tab keeps its actions
behind a **3-dot overflow menu** so the card stays readable however many keys it holds:

- **Reveal key** — shows the stored key, masked until you ask for it, with one-click copy.
- **Regenerate key** (web: *Regen* in the same menu) — issues a new secret in place. The label and
  the user's grants survive; the previous key stops working immediately.

Users without the User Admin role (and Read Only accounts) cannot reveal or regenerate anything.
Keys created before this feature have no stored secret, so revealing one offers to regenerate it
instead.

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

Users without the **Dashboard Read-Write** role see the web server status but not the buttons.
Stopping the web server does not change the *Enabled* setting, so the next proxy start brings it back.

### Performance and web traffic

The Dashboard also shows the same **CPU**, **working set**, **GC gen2**, and **LOH** figures as the
Windows app, refreshed with the rest of the status card.

Web-server requests are logged to the same database as proxied traffic under a **Web** source:
**Traffic** → source **Web** (Admin only) in the dashboard, or the **Web** sub-tab of the Windows
app's **Logs** tab. Both list dashboard page loads, API calls, and rejected requests (401/403)
with the same retention and pruning as proxy logs. Static assets and the dashboard's own polling
endpoints (`/api/status`, `/api/stats`, `/api/logs`, ...) are skipped so the table stays readable,
but an error response is always recorded.

## Logging, Reporting, and Statistics

Every request is logged — nothing lives only in memory. Four independent sources share one SQLite
database and one set of retention rules, so "who called what, when, from which client, with which
key, and what happened" is answerable without correlating files.

### Request log

- **One row per request** — timestamp, model, client model, upstream URL and path, HTTP status,
  duration, streaming or not, prompt/completion/total tokens, the user and API key that made the
  call, the calling client's address, and a **client label** derived from `User-Agent` (VS
  extension, Copilot, browser, script).
- **Source** separates **Proxy** traffic, **MCP** tool calls, **Web** dashboard traffic, and
  **Non-proxied** requests that were refused or not handled, so a scanner hitting an unknown path
  shows up as a row instead of silently vanishing.
- **Detail on demand** — the Requests table loads rows first and fetches bodies, headers, and error
  text only when a row is opened, so the list stays fast against a large database.
- **Live streams** — the same tab tails active SSE/NDJSON streams as they are produced, capped to
  fifteen rows so the request table stays visible; clicking a row opens the captured upstream and
  forwarded tails in a dialog.
- **Refresh** — manual by default, with optional auto-refresh at 5, 10, 30, or 60 seconds.
- **Redaction** — per mapping, `RedactRequestBodies` and `RedactResponseBodies` replace captured
  bodies with a marker (both default **on**), and `RedactSensitiveJsonFields` scrubs keys, tokens,
  and passwords while leaving prompt text readable for diagnostics. `Authorization` headers are
  always redacted.
- **Retention** — `LogRetentionHours` (default `72`) expires rows automatically; `0` keeps them
  forever. Only the newest `MaxLogEntriesWithBodies` entries retain bodies, which bounds memory
  without losing metadata.

### System log

[Serilog](https://serilog.net/) writes rolling files under `Data/logs/app/`
(`AppLogFileSizeLimitMb`, default `10`; `AppLogRetainedFileCount`, default `7`), and the same
events stream into the dashboard's **Sys Log** tab and the Windows app's **Logs → Sys Log** view —
filterable by level, model, and text, with a **Hide heartbeats** switch. Heartbeat ping failures
have their own threshold (`HeartbeatMinimumLevel`, default `Debug`) so an upstream that is simply
switched off cannot bury real errors. Sys Log is Admin-only because it can contain payload
fragments.

### Usage, statistics, and reporting

- **Usage** — token consumption grouped by day, week, or month and by model, scoped to your own
  traffic or, for Admin, to any user with a per-user share breakdown.
- **Statistics** — total requests, total errors, prompt and completion tokens, requests per second,
  and per-model counts, plus a live **connection health** snapshot of heartbeat probes and failures
  for each model.
- **Performance** — CPU, working set, GC gen2 collections, and LOH size sampled for the Dashboard.
- **Web traffic** — dashboard page loads, API calls, and rejected requests (401/403) are logged
  under the **Web** source with the same retention as proxy traffic.
- **Token usage** — the dedicated Usage report described below.

## Token Usage (Web Dashboard)

The web dashboard's **Usage** tab reports token consumption from the request-log database:

- Users without the Admin role see their own usage only (matched by the user that made each request).
- **Admin** users see everyone's usage and can scope the view to *All users*, themselves, or any
  single user, plus a **Usage by user** breakdown with per-user totals and share bars.

Pick a period — **This week** (the default), Last week, This month, Last month, Last 30 days, or
Last 90 days — group the stacked chart by **day / week / month**, and optionally filter to one
model. The **By day** table expands each row into per-model detail (requests, prompt, completion,
total, cached prompt, and reasoning tokens). Week ranges start on Monday, and the queries run
against an index that covers the Usage aggregate, so longer ranges stay fast as the log grows.

Only requests that reported tokens are counted — zero-token probes are excluded. Requests made
without an API key (or before attribution existed) appear as **Unattributed** in the admin
by-user view. Days are grouped in the server's local time zone.

## Test Console (Web Dashboard)

The **Test Console** tab sends a prompt straight through the proxy to a model mapping and streams
the answer back into the page, mirroring the Windows app's Test Console. It is gated by the
**Test Console** role, and the model list it offers is filtered to the models that user can access —
the same per-user grants the proxy enforces, so a Test Console user cannot reach a model they were
not granted (the server answers `403` even if the request is forged).

Pick a model, type a prompt (Ctrl+Enter sends), optionally set a system prompt, temperature, and
repeat penalty, then **Send**. Deltas stream in as they arrive, thinking-mode output lands in its
own pane, token usage and elapsed time show in the status line, and **Cancel** aborts the upstream
request. Each run is recorded in the request log like any other proxied call, marked with the
`(web test console)` path.

Temperature and repeat penalty are pre-filled from the model's mapping and locked when the mapping
fixes that value, so the console cannot send a setting the proxy would overwrite anyway.

## Usage

1. Run `Kaeo LLM Proxy.exe` (Windows) or `./Kaeo LLM Proxy Headless` (macOS/Linux) — the Windows
   build starts minimised to the system tray, the headless build keeps running in the foreground
2. Open the dashboard — double-click the tray icon on Windows, or browse to
   `http://localhost:11435` anywhere
3. Add model mappings with the name your clients will use, the upstream URL, and the upstream
   protocol the backend speaks
4. Use **Fetch Models** to pull available model names from a running backend
5. Create users and API keys on the **Users** tab, and grant each user the models and MCP access
   they should have
6. Point your client at `http://localhost:11434` (or your configured port) — Ollama, OpenAI, and
   Anthropic clients all work against the same listener
7. The proxy routes each request to the correct backend, translates it, and logs it

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

### Platform support

The engine, the web dashboard, and the MCP server are cross-platform. The tray app and the Visual
Studio extension are Windows-only; on Linux and macOS the headless host plus the browser dashboard
give you the same engine with full management UI.

| Component | Windows | Linux | macOS (Apple Silicon) | macOS (Intel) |
|-----------|---------|-------|-----------------------|---------------|
| Proxy engine (`ProxyHostController`) | ✅ | ✅ | ✅ | ✅ |
| Web dashboard | ✅ | ✅ | ✅ | ✅ |
| MCP server | ✅ | ✅ | ✅ | ✅ |
| Headless host (`Kaeo LLM Proxy Headless`) | ✅ | ✅ | ✅ (verified) | ✅ |
| Tray app | ✅ | — | — | — |
| Visual Studio extension | ✅ | — | — | — |
| Code Vector Store module (ONNX + sqlite-vec natives) | ✅ | ✅ | ✅ (verified) | ✅ |
| SSH module | ✅ | ✅ | ✅ | ✅ |

Publish RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`. Module packages with
native dependencies are per-RID — see [docs/Module-Packaging.md](docs/Module-Packaging.md).

"Verified" means exercised end to end on real hardware: proxy endpoints, dashboard login and every
authenticated API, lifecycle controls (proxy / MCP / web server start-stop-restart), HTTPS from a
stored certificate credential, the MCP handshake and tool list, and the native module dependencies
(ONNX Runtime, sqlite-vec, LibGit2Sharp). Linux uses the same code paths and publishes the same
way, but has not yet been run on Linux hardware — treat it as supported but unverified.

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

## Web dashboard

The dashboard is the same UI on every platform and covers everything the tray app does. It is
served by the engine itself — no separate process, no IIS, no Node.

- **Address:** `http://localhost:11435` by default (`ListenPort + 1`). Set `WebDashboard.Port` to
  override, or `WebDashboard.ListenAddress` to bind wider than loopback. Binding to `0.0.0.0`
  exposes the dashboard to your network — keep `WebDashboard.RequireLogin` on.
- **TLS:** set `WebDashboard.Scheme` to `Https` and point `WebDashboard.CertificateCredentialName`
  at a certificate credential created on the **Credentials** tab. The dashboard, the proxy, and
  the MCP endpoint then all serve that certificate.
- **Tabs:** Dashboard (health, CPU/RAM, traffic, and start/stop/restart for the proxy, MCP server,
  and web server), Requests (per-request rows plus live SSE streams), Sys Log, Usage, Models
  (mappings, thinking mode), Credentials, Users (accounts, combinable roles, API keys,
  reveal/regenerate, and per-user model and MCP grants), Test Console, Settings, MCP (tools,
  modules), and Help. Only the tabs your roles unlock are listed.
- **Help:** the Help tab is always present and is filtered by your roles, so it documents only the
  surfaces you can actually reach — a topic appears when you hold the role that unlocks it. It
  opens with a "Your access" card listing your roles and the capabilities they grant.

### Hosting behind IIS on Windows

The dashboard is a self-hosted Kestrel listener, so IIS is only ever a reverse proxy in front of
it. Two options:

1. **Reverse proxy (recommended).** Run the app normally and add a URL Rewrite rule with
   `AR R` (Application Request Routing) that forwards a site or path to `http://localhost:11435`.
   Set `WebDashboard.RequireLogin` to `true` and keep the dashboard bound to loopback so the site
   is the only public surface.
2. **Host the Web project as an IIS application.** Publish
   `src/Kaeo LLM Proxy Web` and configure an *AspNetCoreModuleV2* application. The process must run
   under an account with read/write access to the `Data/` folder (database, logs, module
   packages), and the app pool must not be recycled mid-request if you rely on live SSE streams.

Either way, terminate TLS at IIS and leave `WebDashboard.Scheme` at `Http` so the app does not
double-encrypt; forward `X-Forwarded-Proto` if you need correct absolute links.

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
