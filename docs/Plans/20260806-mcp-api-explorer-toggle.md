# MCP Server Tab: Swagger GUI (API Explorer) Toggle + Clickable Explorer URLs

## Understanding
Add an enable/disable checkbox for the MCP server's API Explorer (Scalar/Swagger GUI) on the
MCP > Server tab, mirroring the existing `_chkApiExplorer` option on the Settings tab. When
enabled, show the explorer URL and make it clickable to launch the default browser. Also fix
the existing Settings-tab API Explorer URL label, which displayed the URL but was not clickable.

## Steps
1. [x] Add `EnableApiExplorer` flag to McpServerSettings and round-trip it in McpServerSettingsRepository
   - New `enable_api_explorer` key in the `mcp_server_settings` key/value table (no schema change)
2. [x] Gate MCP API explorer creation in McpServerService based on the flag
   - `StartAsync` only attaches `McpApiExplorer` when enabled; `StopAsync` clears it
   - Host already 404s `/scalar` and `/openapi/v1/openapi.json` when `ApiExplorer` is null
3. [x] Add MCP API Explorer checkbox and URL label to the MCP Server tab in MainForm.Designer.cs
   - `_chkMcpApiExplorer` + `_lblMcpApiExplorerUrl` rows inserted after the enable checkbox
   - Hand cursor on the Settings-tab URL label
4. [x] Implement URL builders, label update methods, browser launch, and event wiring in MainForm.cs
   - `BuildApiExplorerUrl` / `BuildMcpApiExplorerUrl`, cursor toggling, click handlers
   - `OpenUrlInBrowser` via `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`
5. [x] Build the solution and fix any errors
6. [x] Commit the change to Git
