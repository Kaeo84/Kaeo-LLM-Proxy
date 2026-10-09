# Copilot Instructions — Kaeo LLM Proxy

## Project Overview
This is a **WinForms .NET 10** application that acts as an Ollama-compatible API proxy,
routing requests from Ollama clients to a llama.cpp server's OpenAI-compatible `/v1/` API.

## Repository Layout
```
Kaeo LLM Proxy/
├── Core/
│   ├── Models/          # DTOs: AppSettings, OllamaTypes, RequestLog
│   └── Services/        # OllamaProxyHandler, StatisticsService
├── Infrastructure/
│   └── ProxyServer.cs   # HttpListener loop
├── TrayApplicationContext.cs   # System tray management
├── MainForm.cs / MainForm.Designer.cs   # Tabbed UI
└── Program.cs
```

## Key Design Decisions
- **Single project** — no separate class library; Core/ and Infrastructure/ are folders only.
- **No extra NuGet packages** — uses `System.Text.Json`, `System.Net.HttpListener`, and `System.Net.Http.HttpClient` (all inbox).
- **Tray-only** — `ShowInTaskbar = false` on the form; `ApplicationContext` subclass drives the tray icon.
- **Routing table**

  | Incoming Ollama endpoint      | Forwarded llama.cpp endpoint          |
  |-------------------------------|---------------------------------------|
  | `GET  /api/tags`              | `GET  /v1/models`                     |
  | `POST /api/show`              | `GET  /v1/models/{model}`             |
  | `POST /api/generate`          | `POST /v1/completions`                |
  | `POST /api/chat`              | `POST /v1/chat/completions`           |
  | `POST /api/embeddings`        | `POST /v1/embeddings`                 |

- **Streaming** — Ollama uses NDJSON lines; llama.cpp uses SSE (`data: {...}\n\n`).  
  `OllamaProxyHandler` translates between the two formats.
- **Settings** — persisted as JSON in `%APPDATA%\LlamaCppProxy\settings.json`.

## Coding Conventions
- Follow the global instruction file conventions (file-scoped namespaces, modern C#, async/await, CancellationToken throughout).
- Keep Designer files free of lambdas and complex logic.
- Log entries are thread-safe via `StatisticsService` (ConcurrentQueue).
