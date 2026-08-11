# Tiered SSH activity logging in the MCP logs

Branch: feature/mcp-module
Date: 2026-08-11

## Understanding
The user wants to see the SSH back-and-forth in the MCP logs: connection lifecycle and errors by
default, and with a "Full/verbose" level also the executed commands and their complete output
(e.g. the result of an `ls` the model ran via `ssh_exec`). The level switch belongs on the SSH
module's tab. Entries appear in the existing Logs tab → MCP sub-tab alongside the HTTP rows.

## Assumptions
- Two levels: `Connectivity` (connection opens/reuses/closes + any error from any tool — the
  default) and `Full` (additionally every tool call with arguments/results, incl. command output).
- Entries reuse the existing `RequestLog`/`mcp_requests` pipeline so the list view and the detail
  dialog (ErrorMessage / RequestBody / ResponseBody tabs) work unchanged.
- Modules load in Program.Main before TrayApplicationContext creates the MCP StatisticsService,
  so the host hands modules a late-bound sink: bound once `_mcpStats` exists; tool activity can
  only happen after the MCP server starts, i.e. after binding.
- Contract assembly changes are safe: only the host constructs ModuleContext; all modules in the
  solution rebuild together.
- SSH log rows count toward MCP dashboard stats — acceptable, they are MCP-server activity.

## Approach
- Contracts (`Kaeo LLM Proxy Modules`): new `IMcpActivityLog` + `McpActivityEntry`; extend
  `ModuleContext` with the sink.
- Host: late-bound holder owned by `ModuleHost` (both ModuleContext construction sites) +
  `McpActivityLogAdapter` mapping entries to `RequestLog` (Method=source label,
  Path="{operation} {target}", StatusCode=exit code, DurationMs, ErrorMessage, request/response
  details as bodies with UTF-8 byte counts); bound to `_mcpStats` in TrayApplicationContext.
- SSH module: `SshMcpLogLevel` enum + `SshSettings.McpLogLevel` + `mcp_ssh_settings` key
  (read live per operation). Manager logs connect open/reuse/failure/timeout, transport-death and
  idle closes; `SshTools` logs exec (Full: command + full stdout/stderr; both levels: failures),
  disconnect, and list (Full). Config page gains an "MCP log detail" ComboBox in Tools & Limits
  with immediate save; HelpText updated.
- Host: annotate MCP HTTP rows in `McpServerHost` with the JSON-RPC method and tool name
  ("POST /" rows are currently indistinguishable).

## Key Files
- Kaeo LLM Proxy Modules/IMcpActivityLog.cs (new)
- Kaeo LLM Proxy Modules/ModuleContext.cs
- Infrastructure/Modules/ModuleHost.cs
- Infrastructure/Mcp/McpActivityLogAdapter.cs (new)
- TrayApplicationContext.cs
- Kaeo LLM Proxy SSH/SshModule.cs
- Infrastructure/Mcp/McpServerHost.cs

## Risks & Open Questions
- Exec output can be large; reuse the module's MaxOutputChars truncation for log details.
- Secrets must never reach log details (credential *names*/auth kind only, never material).
- Verification cannot click the UI: set the Full level in the DB, restart, drive tools via MCP,
  query mcp_requests.

## Steps
- [x] 1. Add IMcpActivityLog + McpActivityEntry contract files and extend ModuleContext
- [x] 2. Add the host adapter + late-bound holder and wire it through ModuleHost and TrayApplicationContext
- [x] 3. Add SshMcpLogLevel to the SSH module settings model and repository (key mcp_log_level)
- [x] 4. Implement level-gated activity logging in SshConnectionManager and SshTools
- [x] 5. Add the "MCP log detail" ComboBox to the SSH config page and update HelpText
- [x] 6. Annotate MCP HTTP request logs with JSON-RPC method and tool name in McpServerHost
- [x] 7. Build the solution and fix any errors
- [x] 8. Restart the app, set Full level, drive ssh_list + ssh_connect through MCP tools, verify mcp_requests rows
- [x] 9. Save/update the plan file in Plans/ and git commit the change

## Result
Tiered SSH activity logging is live. At Connectivity level the MCP logs record connection
opens/reuses/closes (including idle and lost-transport closes) plus any tool error or timeout.
At Full level they additionally record every tool call with arguments and complete results,
including full command output (exit code + stdout/stderr). Every MCP HTTP row is now annotated
with the JSON-RPC method and tool name (e.g. "/ tools/call ssh_exec"). Verified end-to-end:
ssh_list and ssh_connect produced the expected SSH rows (error row carried "Connection failed:
Permission denied (password)") alongside annotated HTTP rows.
