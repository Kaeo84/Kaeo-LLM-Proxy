# Make VS Extension tool-calling actually work (MCP + Ollama tool_calls)

## Understanding
Chat streaming worked, but the "model can interface and do work" path was broken end-to-end: MCP servers were never initialized, the Ollama stream parser ignored structured `message.tool_calls`, the runtime parsed a text convention nothing emits, and the request payload never included tool definitions. Fixed the whole loop so a tool-capable model can call MCP tools and receive results.

## Findings (audit)
- Add model / select model / choose agent / send / stream tokens: already wired.
- `McpServerManager.InitializeAsync()` was never called -> `_servers` empty -> zero tools.
- `StreamChatCoreAsync` read only `message.content`, ignored `message.tool_calls`.
- `AgentRuntime.ParseToolCalls` scanned text for `{"tool_call":...}` (nothing emits it) -> always 0 calls.
- `BuildChatPayload` sent tools only when `agent.Tools != null`, but Agent/Plan use `Tools = null` (= all) -> tools never sent.
- History round-trip used the wrong JSON shape (not Ollama `tool_calls` / `role:"tool"` + `tool_call_id`).
- Interactive `RequestPermission` auto-approved and ignored the mode.

## Approach
1. `OllamaApiClient.cs` - `ChatChunk` now carries `JsonNode? ToolCalls`; parser reads `message.tool_calls` from each NDJSON line.
2. `AgentRuntime.cs` - `AgentMessage` reworked to Ollama shape (`ToolCalls` / `ToolCallId`); loop drives from structured tool calls; text `ParseToolCalls` replaced with a `message.tool_calls` parser; tools sent whenever the agent isn't explicitly tool-less.
3. `ToolWindowViewModel.cs` - shared `McpServerManager` injected, `await _mcp.InitializeAsync()` in `LoadAsync`; Interactive `RequestPermission` shows a real themed `ShowConfirmAsync` dialog.
4. Build clean (0 errors, no new warnings).

## Steps
- [x] 1. Carry structured tool_calls through the Ollama stream parser
- [x] 2. Rework AgentRuntime message model + tool loop + payload tool gating
- [x] 3. Initialize MCP in the view model and wire a real permission prompt
- [x] 4. Build the extension and resolve diagnostics
- [x] 5. Commit and push as Kaeo84

## Verification (user)
- Needs a running proxy + a tools-capable model + at least one enabled MCP server (configured in Settings -> Models, and MCP servers in settings.jsonc).
- Pick Agent/Plan mode, send a prompt that requires a tool; expect a tool line in the transcript and, in Interactive mode, an "Allow tool call" confirm dialog.
