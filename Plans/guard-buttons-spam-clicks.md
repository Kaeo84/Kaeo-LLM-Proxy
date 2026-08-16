# Guard buttons against spam clicks

## Understanding
Prevent rapid repeated clicks on Start/Stop/Restart (proxy + MCP dashboard), MCP Apply, Reset (stats / MCP stats / heartbeats), Save (listener / heartbeats), and Refresh/Clear logs from starting overlapping or re-entrant operations.

## Approach
- `_proxyOperationInProgress` / `_mcpDashOperationInProgress` flags checked at handler entry; `RefreshStatus` / `RefreshMcpDashboardStatus` keep the trios disabled while an operation is in flight (also covers status-event refreshes mid-operation).
- MCP settings applies coalesced via `_applyingMcpSettings` + `_mcpSettingsApplyPending`.
- `RunOnceWhileDisabled` helper for synchronous single-button handlers (blocks re-entry through MessageBox message pumps).
- Reviewed as safe: modal-popping buttons (Add/Configure mapping, Browse DB, log details) — modal loop disables owner; Test Send already self-disables; Test Cancel intentionally always enabled; Remove/Duplicate mapping are synchronous, one deliberate action per click.

## Steps
1. [x] Add guard flags and RunOnceWhileDisabled helper in MainForm.cs
2. [x] Guard proxy Start/Stop/Restart handlers and RefreshStatus
3. [x] Guard MCP dashboard Start/Stop/Restart, ApplyMcpServerSettingsAsync, and RefreshMcpDashboardStatus
4. [x] Guard synchronous reset/save/refresh/clear handlers
5. [x] Build and verify
6. [x] Commit
