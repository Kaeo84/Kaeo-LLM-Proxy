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

## Round 2 - settings file sharing violation (IOException 0x80070020)
Adding the first connection threw "file is being used by another process" from `ExtensionSettingsStore.SaveAsync`. Cause: multiple `ExtensionSettingsStore` instances (tool window, settings window, MCP manager) plus the 500ms debounced auto-save can write the same file concurrently, and a second devenv instance (main + Exp with the extension installed) can hold it open cross-process.

Fixes in `ExtensionSettingsStore.cs`:
- Static `SemaphoreSlim` serializes writes in-process.
- Writes use `FileShare.Read` + retry/backoff (8 attempts, 50ms steps) for transient cross-process locks; persistent failure still surfaces via the themed error popup.
- Reads use `FileShare.ReadWrite` and retry transient `IOException`/`JsonException` before falling back to defaults (prevents a truncated read followed by a save from wiping real settings).
- [x] Committed + pushed as Kaeo84.

## Round 3 - duplicate model dropdown entries + empty transcript
Symptoms: model combo listed every model 3x; transcript showed only an empty "assistant" line plus the status line (user prompt never posted, response text never rendered).

Causes:
- `SettingsWindow.ModelsChanged` fires per add/refresh/save; each fire starts `_ = LoadAsync()`. Overlapping runs each `Models.Clear()` then `await` the /api/tags fetch before adding, so all clears land before any adds -> N interleaved copies.
- `SendAsync` never added a `user` ChatLine, and `ChatLine.Text` had no change notification, so streamed deltas and the final `FinalText` assignment never re-rendered the bound ListView row.

Fixes:
- `ToolWindowViewModel.cs`: `LoadAsync` now serializes through a `SemaphoreSlim` gate into `LoadCoreAsync`; models are collected into local lists (label-deduped via HashSet) across awaits and swapped into `_modelSelections`/`Models` in one synchronous pass; `SendAsync` echoes the prompt as a `user` line; `ChatLine` implements `INotifyPropertyChanged` for `Text`.
- `ToolWindowControl.xaml.cs`: transcript auto-scrolls to the newest line, coalesced via background-priority dispatcher invoke so per-token deltas scroll once per burst.
- [x] Build clean (0 errors).
- [x] Committed + pushed as Kaeo84.
