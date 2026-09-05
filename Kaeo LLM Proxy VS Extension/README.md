# Kaeo VS 2026 Extension

A Visual Studio 2026 extension with a GitHub Copilot-style chat panel that pairs with the
[Kaeo LLM Proxy](../README.md). It provides an agent/mode/model pill bar, a streaming chat
transcript, a multi-tab settings modal, and MCP tool integration — all routed through the
proxy's Ollama-compatible API.

## Projects

| Project | Purpose |
|---------|---------|
| `Kaeo LLM Proxy VS Extension.Core` | Host-agnostic client library: Ollama API client, MCP server manager, agent runtime, settings store, logger |
| `Kaeo LLM Proxy VS Extension` | VSIX project: WPF tool window, settings modal, VS package registration, command bar |

## Architecture

```
+--------------------------------------------------+
|  Kaeo LLM Proxy VS Extension (VSIX)              |
|                                                  |
|  +----------------+  +-------------------------+ |
|  | ToolWindow     |  | SettingsWindow          | |
|  | (WPF, bottom   |  | (4 tabs: General,      | |
|  |  docked)       |  |  Models, Agents, MCP)  | |
|  +-------+--------+  +-----------+------------+ |
|          |                         |            |
|          v                         v            |
|  +------------------------------------------+   |
|  | ToolWindowViewModel                       |   |
|  | (binds UI to ChatEngine, streaming,      |   |
|  |  live model pull, permission routing)    |   |
|  +-------------------+----------------------+   |
|                      |                          |
+----------------------+--------------------------+
					   v
+------------------------------------------+
| Kaeo LLM Proxy VS Extension.Core        |
|                                          |
|  +----------+  +-----------+  +-------+ |
|  | ChatEngine|  |AgentRuntime|  |Mcp   | |
|  | (facade) |  | (loop,    |  |Server | |
|  |          |  |  modes,   |  |Manager| |
|  |          |  |  tools)   |  |       | |
|  +----+-----+  +-----+-----+  +---+---+ |
|       |              |           |       |
|       v              v           v       |
|  +-----------------------------------+   |
|  | OllamaApiClient                    |   |
|  | /api/tags, /api/chat (NDJSON)     |   |
|  +-----------------------------------+   |
+------------------------------------------+
					   |
					   v
		 Kaeo LLM Proxy (localhost)
```

- **No Copilot SDK runtime dependency.** The Copilot SDK is used only as a design reference
  for agent/mode/tool interaction patterns.
- **Model calls** go through the proxy's Ollama-compatible API (`/api/chat` NDJSON streaming,
  `/api/tags`). OLLAMA standards only; OpenAI is not used yet.
- **Tool execution** goes through MCP clients (`McpServerManager`: HTTP Streamable JSON-RPC).
- **VS interaction** uses standard VSSDK (tool window, command bar).

## Agent Modes

| Mode | Behavior |
|------|----------|
| **Interactive** | Each tool call prompts the user for approval before executing |
| **Bypass** | All tool calls are auto-approved |
| **AutoPilot** | All tool calls auto-approved; if the model signals "not done", the runtime auto-continues (capped at 5 continuations) |

## Built-in Agents

| Agent | Tools | Purpose |
|-------|-------|---------|
| **Agent** | All | Full coding agent with tool access |
| **Ask** | None | Concise Q&A, no tool use |
| **Plan** | All | Structured implementation plans (markdown: Understanding, Assumptions, Approach, Key Files, Risks, Steps) |

User-defined agents can be added via the Settings → Agents tab and are persisted to `settings.jsonc`.

## Settings

All configuration lives in a single JSONC file at `%APPDATA%\KaeoVsExtension\settings.jsonc`:

```jsonc
{
  "defaults": { "agent": "Agent", "mode": "Interactive", "model": "...", "autoAttachContext": true },
  "logging": { "level": "Information" },
  "connections": [
	{
	  "name": "Desktop",
	  "baseUrl": "http://localhost:8388",
	  "apiKey": null,
	  "enabled": true,
	  "models": [
		{ "name": "qwen-27b", "capabilities": ["completion", "tools"], "contextSize": 300000, "pinned": true }
	  ]
	}
  ],
  "agents": [
	{ "name": "MyAgent", "description": "...", "systemPrompt": "...", "tools": [], "defaultModel": "..." }
  ],
  "mcpServers": [
	{
	  "name": "Kaeo Proxy",
	  "transport": "http",
	  "url": "http://localhost:8389/mcp",
	  "enabled": true,
	  "tools": [ { "name": "code_search", "description": "...", "enabled": true } ]
	}
  ]
}
```

### Models tab
- Add a connection: name + base URL + optional API key
- **Refresh models** re-pulls `/api/tags` per connection and updates the tree in place
- Models are grouped by connection; tool-capable models (Ollama `"tools"` capability) are marked `[tools]`
- The model pill bar shows `connection / model`; requests route to the owning connection

### MCP tab
- Multiple MCP servers (HTTP Streamable + stdio transports)
- Auto-pulls tool definitions via `tools/list` on add and refresh, cached in JSONC
- Per-server enable/disable toggle; per-tool checkboxes

## Build & Install

### Prerequisites
- Visual Studio 2026 (Enterprise/Professional/Community)
- .NET 10 SDK
- Kaeo LLM Proxy running with at least one enabled model mapping

### Build
```
dotnet build "Kaeo LLM Proxy VS Extension\Kaeo LLM Proxy VS Extension.csproj" -p:Configuration=Release
```

Note: `GeneratePkgDefFile` is set to `false` in the csproj because the CLI `CreatePkgDef`
tool (.NET Framework) cannot load net10.0-windows type references. Build through the
**Visual Studio IDE** to generate the `.pkgdef` registration file.

### Install
1. Build the VSIX project in **Release** from the VS IDE
2. Find the `.vsix` at `Kaeo LLM Proxy VS Extension\bin\Release\net10.0-windows10.0.22000.0\win-x64\`
3. In VS: **Extensions → Manage Extensions → gear → Install from File** → select the `.vsix`
4. Restart VS
5. **View → Tool Windows → Kaeo Assistant**

### Quick test (F5)
1. Set the startup project to `Kaeo LLM Proxy VS Extension`
2. Press **F5** — VS opens an experimental instance with the extension loaded
3. In the experimental instance: **View → Tool Windows → Kaeo Assistant**
4. Click the gear (⚙) → **Models** tab → add your proxy connection
5. Select a model, type a prompt, press Enter

## Known Limitations
- Theme compliance: WPF colors are not yet bound to `VsBrushes`/theme dictionaries
- Command bar buttons (Agent/Ask/Plan) are not yet registered in `Commands.vsct`
- stdio MCP transport is a stub (HTTP Streamable is implemented)
- Interactive-mode permission dialog auto-approves (real confirm UI not yet built)
- OpenAI-compatible endpoint support is deferred
