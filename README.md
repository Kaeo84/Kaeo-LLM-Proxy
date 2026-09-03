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

The proxy supports context compaction to manage large conversation histories and prevent context overflow errors.

### Automatic Compaction

When enabled, the proxy automatically compacts conversation history before it exceeds the model's context window:

- **For non-Copilot clients**: Proactive auto-compaction summarizes conversation history when it approaches the context limit
- **For GitHub Copilot**: The proxy detects Copilot requests and skips auto-compaction, allowing Copilot's native `/compact` flow to manage context

This prevents the proxy from interfering with Copilot's internal state management while still providing compaction for other clients.

### Manual Compaction Endpoint

A manual compaction endpoint is available for explicit context management:

```
POST /v1/chat/completions/compact
```

This endpoint accepts a chat completion request and returns a compacted version. It must be enabled in settings (`EnableManualCompactionEndpoint`).

### Configuration

Three settings control context compaction behavior:

- **`EnableCopilotNativeCompaction`** (default: `true`): Detects GitHub Copilot requests and skips proactive auto-compaction
- **`EnableAutoCompaction`** (default: `true`): Enables proactive auto-compaction for non-Copilot clients
- **`EnableManualCompactionEndpoint`** (default: `false`): Exposes the manual `/v1/chat/completions/compact` endpoint

### Compact Model Configuration

To use compaction, configure a compact model in your model mapping:

1. Add a model mapping for a smaller/faster model (e.g., `gpt-3.5-turbo` or a local small model)
2. In your main model mapping, set `ContextSummarizeModelId` to reference the compact model
3. The proxy will automatically redirect compaction requests to the compact model

This allows you to use a large model for regular requests while using a smaller, faster model for compaction tasks.

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
