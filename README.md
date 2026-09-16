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

| OpenAI endpoint          | Method | Purpose                                                                      |
|--------------------------|--------|------------------------------------------------------------------------------|
| `/v1/models`             | GET    | Lists enabled model mappings in OpenAI format, including `context_length`.   |
| `/v1/models/{model}`     | GET    | Returns a single enabled model mapping in OpenAI format.                     |
| `/v1/chat/completions`   | POST   | Forwards chat completion requests to the selected upstream llama.cpp server. |
| `/v1/completions`        | POST   | Forwards completion requests to the selected upstream llama.cpp server.      |
| `/v1/embeddings`         | POST   | Forwards embedding requests to the selected upstream llama.cpp server.       |

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

## Usage

1. Run `Kaeo LLM Proxy.exe` — it starts minimised to the system tray
2. Double-click the tray icon (or right-click → Open) to open the dashboard
3. Add model mappings with the name your clients will use and the upstream llama.cpp URL
4. Use **Fetch Models** to pull available model names from a running llama.cpp server
5. Point your Ollama-compatible client at `http://localhost:11434` (or your configured port)
6. The proxy routes each request to the correct llama.cpp instance and translates the response

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

- Windows 10 version 22000 (21H2) or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

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
