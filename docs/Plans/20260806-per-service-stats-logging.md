# Per-Service Stats and Logging (Proxy vs MCP)

Date: 2026-08-06

## Understanding
The aggregate stats panel (Total Requests / Errors / Prompt Tokens / Completion Tokens / Req/s / Reset Stats) must move into the Proxy Status group and be tracked per service: the MCP server gets its own stats counters and request logging like the proxy, displayed in a matching panel inside the MCP Status group. The Logs tab gains two sub-tabs (Proxy and MCP), each with its own virtual-mode list.

## Assumptions
- MCP log entries reuse the RequestLog model; token/model/summarization fields stay zero/empty for MCP.
- MCP requests table mirrors the `requests` schema so existing mappers/parameter helpers are reused; no exceptions are persisted for MCP (errors are HTTP-level, captured as ErrorMessage/StatusCode).
- MCP logging captures summary fields only (method/path/status/duration/bytes); request/response bodies are not captured (SSE responses make this impractical).
- The shared Logs bottom button bar acts on the selected sub-tab (Details/Clear), while Refresh and auto-refresh update both lists.
- A new `mcp_requests` table via CREATE TABLE IF NOT EXISTS needs no migration for existing databases.

## Approach
- `Core\Models\RequestLog.cs`: `LogSource { Proxy, Mcp }` enum.
- `Infrastructure\AppDatabase.cs`: `mcp_requests` table + index in baseline DDL; Insert/LoadRecent/LoadFullLogEntry/DeleteOlderThan/ClearLogs parameterized by LogSource (table name switch).
- `Core\Services\StatisticsService.cs`: LogSource ctor param; source-aware seeding/persistence/pruning/GetException; heartbeat seeding Proxy-only.
- `Infrastructure\Mcp\McpServerService.cs`: accept + expose StatisticsService; pass to host.
- `Infrastructure\Mcp\McpServerHost.cs`: log every request via AddLog.
- `TrayApplicationContext.cs`: create/dispose MCP stats; reorder McpServerService construction.
- `MainForm.Designer.cs`: stats panels into groups; Logs sub-tabs + MCP list.
- `MainForm.cs`: MCP stats/logs refresh + handlers.
- `Infrastructure\HelpPages.cs`: Dashboard/Logs help text.

## Steps
- [x] 1. Save plan copy to Plans folder
- [x] 2. Add LogSource enum to Core\Models\RequestLog.cs
- [x] 3. AppDatabase: add mcp_requests table + index to baseline DDL
- [x] 4. AppDatabase: parameterize Insert/LoadRecent/LoadFullLogEntry/DeleteOlderThan/ClearLogs by LogSource
- [x] 5. StatisticsService: LogSource ctor param; source-aware seeding/persistence/pruning/GetException; heartbeat seeding Proxy-only
- [x] 6. McpServerService: accept and expose StatisticsService; pass into McpServerHost
- [x] 7. McpServerHost: log every request (timing, status, bytes) via the stats service
- [x] 8. TrayApplicationContext: create MCP StatisticsService, reorder McpServerService construction, dispose MCP stats
- [x] 9. Designer: move _tlpStats into Proxy Status group; add MCP stats panel to MCP Status group
- [x] 10. Designer: rebuild Logs tab with Proxy/MCP sub-tabs and _lstMcpLogs + columns
- [x] 11. MainForm: MCP stats refresh/reset handlers and settings propagation
- [x] 12. MainForm: MCP logs cache, virtual-item handler, clear/details branching, timer tick updates
- [x] 13. Update Dashboard and Logs help text
- [x] 14. Build and fix compile errors
- [x] 15. Git commit
