# Chat Layout, Debug Output Window, Settings Chrome, and MCP Save/Test

## Understanding
Six asks across the VS extension: (1) the Mode dropdown should be a fixed ~16-character width that never grows, with the Model dropdown taking the remaining width without overflowing; (2) the Settings button should become icon-only to save space; (3) a debug icon in the top-left of the tool window, opposite the export button, opening a plain text-area window that captures all plugin debug/info/exception output when running under a debugger; (4) the settings window fixed at 1024x800; (5) a Close button in the bottom-right of the settings window, visible on every tab; (6) in the MCP tab, a Save button and a Test Connection button, with errors hoverable (tooltip) and click-to-copy including call info.

## Assumptions
- "Debug mode" means a debugger is attached (`Debugger.IsAttached`), which is exactly the F5 experimental-instance case. A ring buffer is kept so opening the window later still shows prior lines.
- The debug window is a real VS tool window (`ToolWindowPane` + `[ProvideToolWindow]`) so it docks/themes like the chat window; the toolbar icon shows-or-focuses it.
- The Mode combo gets a fixed width (no growth); Model fills the rest via a star column with `MinWidth=0` so long names clip instead of pushing the layout.
- The settings window is set to 1024x800 and kept resizable (`CanResize`) rather than truly locked, so it cannot become unusable on a small or high-DPI display; the Close button is the explicit dismiss affordance the user asked for.
- "Test connection" for `http` performs a real MCP `initialize` handshake (proves reachability + protocol); `stdio` is not implemented in the manager yet, so the test reports that clearly rather than silently passing.
- The MCP tab keeps auto-saving (consistent with the rest of the window); the explicit Save button is an additional affordance that persists immediately and reports status.
- Error tooltip/copy reuses the existing `FlattenException` detail (type + message + inner chain + stack) so "call information" is included.

## Approach
Chat layout: convert the pills `StackPanel` to a three-column Grid (Agent Auto / Mode fixed / Model star), shrink `GearButton` to an icon-only flat button, and turn the top toolbar into a DockPanel so the new Debug button sits left and Export stays right; the Debug click calls `VsPackage.Instance.ShowToolWindowAsync`.

Debug pipeline: a new `Core/DebugLog.cs` static sink (gated on `Debugger.IsAttached`, ring buffer + `LineWritten` event, `Info/Debug/Error`), a new `Debug/DebugOutputPane.cs` + `DebugOutputControl.xaml(.cs)` (read-only monospace TextBox that replays the buffer on load and appends thread-safely), registered via `[ProvideToolWindow]` with a static `VsPackage.Instance`. Existing error/info sites (settings `ReportError`, chat send catch, MCP pull/execute failures, `ToolWindowViewModel.SendAsync` catch, plus `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`) funnel into it.

Settings chrome: set `Width=1024 Height=800` and add a bottom bar row (outside the `TabControl`, so it shows on every tab) with a right-aligned Close button.

MCP tab: add `LastErrorDetail`/`StatusMessage`/`SetError(Exception)` to `McpServerViewModel`, a `TestConnectionAsync` (real `initialize` for http) to `McpServerManager`, and Save + Test Connection buttons plus a hover/click-to-copy error `TextBlock` in the tab.

## Key Files
- Kaeo VS Extension/ToolWindow/ToolWindowControl.xaml - pills grid, icon-only settings button, debug toolbar button
- Kaeo VS Extension/ToolWindow/ToolWindowControl.xaml.cs - debug button handler, funnel chat errors to DebugLog
- Kaeo VS Extension/Core/DebugLog.cs (new) - debug-mode logging sink + ring buffer
- Kaeo VS Extension/Debug/DebugOutputPane.cs, DebugOutputControl.xaml(.cs) (new) - the debug tool window
- Kaeo VS Extension/VsPackage.cs - [ProvideToolWindow], static Instance, global exception hooks
- Kaeo VS Extension/Settings/SettingsWindow.xaml - 1024x800, persistent Close bar, MCP Save/Test buttons + error tooltip
- Kaeo VS Extension/Settings/SettingsWindow.xaml.cs - Close/Save/Test handlers, error detail, DebugLog
- Kaeo VS Extension/Settings/McpServerViewModel.cs - error detail + status message
- Kaeo VS Extension/Core/McpServerManager.cs - TestConnectionAsync, log pull/execute failures

## Risks & Open Questions
- Showing the debug window needs a package handle; if `VsPackage.Instance` is null (window shown before package init) the click must no-op safely.
- DebugLog writes can come from background threads (`ConfigureAwait(false)`), so the control must marshal to its Dispatcher.
- An `initialize` handshake needs a valid `protocolVersion`; if a server rejects it the test may report failure even though `tools/list` would work - fall back to `tools/list` on an initialize error before declaring failure.
- Fixed 1024x800 could overflow a very small screen; keeping the window resizable mitigates this.

## Steps
- [ ] 1. Add `Core/DebugLog.cs` - debug-mode sink with ring buffer and Info/Debug/Error methods
- [ ] 2. Add `Debug/DebugOutputPane.cs` and `Debug/DebugOutputControl.xaml(.cs)` - the plain text-area tool window
- [ ] 3. Register the debug window in `VsPackage.cs` and add a static Instance plus global exception hooks
- [ ] 4. Add the Debug toolbar button to the tool window and open the debug pane from it
- [ ] 5. Rework the tool window pills bar into a Grid (fixed Mode, filling Model) and make the Settings button icon-only
- [ ] 6. Set the settings window to 1024x800 and add a persistent bottom Close bar
- [ ] 7. Extend `McpServerViewModel` with error detail and a status message
- [ ] 8. Add `McpServerManager.TestConnectionAsync` (http initialize with tools/list fallback) and log failures
- [ ] 9. Add MCP Save + Test Connection buttons and a hover/click-to-copy error display in the tab
- [ ] 10. Wire the MCP and settings/chat error paths to DebugLog and add the Save/Test/Close/copy handlers
- [ ] 11. Build the extension and resolve any compile or XAML errors
- [x] 12. Commit and push to main as Kaeo84
