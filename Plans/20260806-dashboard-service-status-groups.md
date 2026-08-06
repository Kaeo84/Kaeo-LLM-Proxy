# Dashboard Service Status Groups

Date: 2026-08-06
Branch: feature/mcp-module

## Understanding
The Dashboard tab's Proxy Status GroupBox is broken (a Dock=Fill Panel inside an AutoSize GroupBox collapses, dropping its child controls visually). Fix that group, show the *running* listen IP/port for the proxy (updated only after a restart), add a new MCP status group below it with its own running IP/port and Start/Stop/Restart buttons, move the proxy Start/Stop/Restart buttons into the Proxy Status group (removing the duplicate bottom button row), and move the CPU/RAM Process Performance group to the top.

## Assumptions
- "Running" values = address/port actually bound at service start, not the saved settings (saved-but-not-restarted changes must not update the display).
- The bottom `_flpDashboardButtons` (large Start/Stop/Restart) is removed; buttons live inside their service groups.
- MCP Start starts the server even when the persisted Enabled flag is off (mirrors proxy Start vs AutoStartProxy) without changing the persisted flag.
- No test project exists; verification is a full build.

## Approach
- `Infrastructure\ProxyServer.cs`: add `ListenAddress`/`ListenPort` properties set on successful bind.
- `Infrastructure\Mcp\McpServerService.cs`: add `ListenAddress`/`ListenPort`, `StartAsync(bool forceStart = false, ...)`, and `RestartAsync()`.
- `MainForm.Designer.cs`: replace broken `_pnlStatus` with `_tlpStatus` (Status / Listen IP / Port + buttons row); add `_grpDashMcp` group with own TLP, labels, Start/Stop/Restart buttons; reorder `_tlpDashboard` rows to Perf -> Proxy Status -> MCP Status -> Stats -> filler; remove old bottom button row.
- `MainForm.cs`: refresh both groups from services' runtime state; wire MCP button handlers.

## Steps
- [x] 1. Save plan copy to Plans folder
- [x] 2. Add running ListenAddress/ListenPort properties to ProxyServer (set after successful listener start)
- [x] 3. Add running address/port, forced start, and RestartAsync to McpServerService
- [x] 4. Rebuild Dashboard layout in MainForm.Designer.cs
  - [x] remove `_pnlStatus`, `_flpDashboardButtons`, `_btnDashStart/_btnDashStop/_btnDashRestart`
  - [x] add `_tlpStatus` inside `_grpStatus` with Status/Listen IP/Port rows and `_flpStatusButtons` row
  - [x] add `_grpDashMcp` with `_tlpDashMcp`, caption/value labels, `_flpDashMcpButtons` + 3 buttons
  - [x] reorder `_tlpDashboard` rows: Perf, Proxy Status, MCP Status, Stats, filler; enable AutoScroll
- [x] 5. Update MainForm.cs status logic and handlers
  - [x] RefreshStatus uses `_server.ListenAddress/ListenPort` and proxy-group buttons only
  - [x] add RefreshMcpDashboardStatus + async MCP Start/Stop/Restart handlers
  - [x] hook refresh into OnMcpStatusChanged and OnLoad
- [ ] 6. Build the solution and fix any compile errors
- [ ] 7. Git commit the change with a descriptive message
