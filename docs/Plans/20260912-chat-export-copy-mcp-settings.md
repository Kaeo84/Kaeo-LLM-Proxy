# Chat UX, Transcript Export, and MCP Settings Tab

## Understanding
Four asks for the Kaeo VS Extension: (1) pressing Enter in the chat input should send, (2) the transcript should allow selecting and copying multiple messages, (3) an icon button at the top of the tool window should export the whole conversation to Markdown / plain text / HTML, and (4) a new Settings tab to add/remove MCP servers and enable/disable the tools underneath each server. Plus two research questions about matching GitHub's built-in tool list and driving Visual Studio the way the Copilot extension does.

## Assumptions
- Enter-to-send is broken because `Key.Enter` is the numpad key; `Key.Return` is the main Enter. Both will be accepted, `Shift+Enter` still inserts a newline.
- "Select and copy multiple messages" means row-level multi-select on the transcript `ListView` plus `Ctrl+C` and a context menu; per-character text selection inside a streamed `TextBlock` is not available in WPF without replacing the transcript with a `FlowDocument`/`RichTextBox`, which is out of scope.
- Export writes through `Microsoft.Win32.SaveFileDialog`; formats are `.md`, `.txt`, `.html`. HTML is a simple self-contained document with escaped text (no markdown renderer dependency).
- MCP transports offered match what `McpServerManager` supports today: `http` (implemented) and `stdio` (placeholder). Tool runtime naming stays `<server>-<tool>`.
- No unit tests: `Kaeo LLM Proxy.Tests` is `net10.0-windows` and references only the WinForms app, so it cannot reach the `net48` VSIX internals.
- `ModelsChanged` is reused as the "settings persisted, refresh yourself" signal so the tool window re-runs `McpServerManager.InitializeAsync` after MCP edits.

## Approach
Chat side: fix the key check in `ToolWindowControl.xaml.cs`, add a small `TranscriptFormatter` next to `ChatLine` that turns the `Lines` collection into plain text / Markdown / HTML so copy and export share one source of truth, then add a top toolbar row with an export icon button (Segoe MDL2 glyph + `ContextMenu` of the three formats) and switch `MessageList` to `SelectionMode="Extended"` with `Ctrl+C` and a copy context menu.

Settings side: mirror the existing Connections pattern - new `McpServerViewModel`/`McpToolViewModel`, a fourth `TabItem` in `SettingsWindow.xaml` with a server list (New/Delete/Enabled), a server editor (name, transport, url, api key, command, args) and a tool list with per-tool enable checkboxes plus a Refresh button that calls `McpServerManager.PullToolsAsync`. `SettingsWindow.xaml.cs` loads them, wires them for the existing debounced auto-save, and maps them back in `SaveNow`.

Core fix required to make the toggles meaningful: `McpServerManager.cs` must persist the settings instance it actually mutated (today it re-loads and saves that, dropping the pull) and must merge pulled tools by name so an existing `Enabled=false` survives a refresh.

## Key Files
- Kaeo VS Extension/ToolWindow/ToolWindowControl.xaml - transcript list, new top toolbar, copy context menu
- Kaeo VS Extension/ToolWindow/ToolWindowControl.xaml.cs - Enter-to-send fix, copy/export handlers
- Kaeo VS Extension/ToolWindow/TranscriptFormatter.cs (new) - shared plain/Markdown/HTML rendering
- Kaeo VS Extension/Settings/SettingsWindow.xaml - new MCP tab
- Kaeo VS Extension/Settings/SettingsWindow.xaml.cs - load/wire/save MCP servers
- Kaeo VS Extension/Settings/McpServerViewModel.cs, McpToolViewModel.cs (new) - bindable MCP rows
- Kaeo VS Extension/Core/McpServerManager.cs - persist pulled tools, preserve Enabled on refresh

## Risks & Open Questions
- `PullToolsAsync` currently saves inside the manager; changing what it persists could surprise the tool window's own refresh path - keep the signature and only fix the object it saves.
- The settings window and tool window each construct their own `McpServerManager`/store; a pull triggered from Settings only becomes visible in chat after `ModelsChanged` reloads the manager.
- HTML export escapes text but does not render markdown, so assistant markdown stays literal - acceptable for a transcript archive.
- stdio servers cannot list tools yet (placeholder returns empty), so the tool list will look empty for them.

## Steps
- [x] 1. Fix Enter-to-send - accept `Key.Return` as well as `Key.Enter` in the prompt box handler
- [x] 2. Add `TranscriptFormatter.cs` - plain text, Markdown, and HTML rendering over `ChatLine` items
- [x] 3. Add the tool window top toolbar - export icon button with a Markdown/Plain text/HTML menu and `SaveFileDialog`
- [x] 4. Enable multi-select and copy - `SelectionMode=Extended`, `Ctrl+C`, and a copy context menu on the transcript
- [x] 5. Add `McpToolViewModel` and `McpServerViewModel` - bindable server and tool rows
- [x] 6. Add the MCP `TabItem` XAML - server list, server editor, tool list with enable checkboxes and Refresh
- [x] 7. Wire the MCP tab in `SettingsWindow.xaml.cs` - load, wire for auto-save, add/delete/refresh, map back on save
- [x] 8. Fix `McpServerManager` - persist the mutated settings object and merge pulled tools by name to keep `Enabled`
- [x] 9. Build the solution and resolve any compile or XAML errors
- [x] 10. Save the plan to `Plans/` and commit + push to `main` as Kaeo84
- [x] 11. Answer the copilot-sdk questions - built-in tool set/toggles and driving Visual Studio over MCP
