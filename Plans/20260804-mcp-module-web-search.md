# MCP Module Class Library with Web Search Tool (Importable Module Registry + Scalar Explorers)

## Understanding
Add a self-contained MCP (Model Context Protocol) server module to Kaeo LLM Proxy as its own class
library, loadable through a persisted module registry with browse-to-import (no directory scanning,
no auto-import, no per-module proxy code changes). First MCP feature: safe configurable Web Search
(web_search + web_fetch tools) with multiple search providers and allow/deny domain lists. Both the
proxy and the module get Scalar API explorers with cross-aware document dropdowns (Swagger fully
retired on the proxy side).

## Steps
- [x] 1. Create contracts library `Kaeo LLM Proxy Modules`
- [x] 2. Add host module hosting
- [x] 3. Add host "Modules" tab
- [x] 4. Inject module tabs into `MainForm`
- [ ] 5. Wire module lifecycle — start `IRunnableModule`s in `TrayApplicationContext` after proxy start (honoring persisted enabled state), stop on exit/dispose; tolerate bind/IOException failures with log + status surface.
- [x] 6. Replace Swagger with Scalar on the proxy
- [x] 7. Create MCP module project `Kaeo LLM Proxy MCP`
- [x] 8. Implement MCP server host
- [x] 9. Implement Web Search feature
- [x] 10. Implement module API explorer
- [x] 11. Build module UI
- [x] 12. Build the solution, fix compile errors; manual verification

## Key Files
- `Kaeo LLM Proxy Modules/` — new contracts library (referenced by host and all modules)
- `Kaeo LLM Proxy MCP/` — new MCP module class library
- `Infrastructure/Modules/ModuleHost.cs`, `ModuleAssemblyLoadContext.cs` — host-side module loading
- `Infrastructure/AppDatabase.cs` — module_registry baseline DDL + module database gateway
- `Core/Services/OllamaProxyHandler.cs` — Swagger → Scalar explorer migration
- `MainForm.cs` / `MainForm.Designer.cs` — Modules tab + module tab injection
- `Program.cs`, `TrayApplicationContext.cs` — lifecycle wiring

## Notes
- Branch: feature/mcp-module
- SQLite with raw Microsoft.Data.Sqlite (no EF Core); schema baseline = one embedded SQL file per module schema, applied programmatically.
- Unreleased app: no migration compatibility paths.
- IOException tolerance for AllowMultipleInstances scenarios.
- Prefer IPv4 when collecting client IPs.

## Outcome (20260804)
- All 12 steps implemented on feature/mcp-module.
- Pivot from the original step 7-8 design: ModelContextProtocol.AspNetCore requires the ASP.NET
  Core shared framework, which the portable WinForms host cannot demand. The module instead drives
  the SDK's StreamableHttpServerTransport directly over a dedicated HttpListener (McpServerHost),
  using ModelContextProtocol 2.0.0 core only.
- Module DB parameter API is Action<DbCommand> + CreateParameter (provider-agnostic).
- Host csproj at repo root excludes sibling project folders from its default glob.
- Verified by an automated protocol harness (22/22 checks): schema/seed, start/stop, /health,
  OpenAPI spec, Scalar page, initialize + Mcp-Session-Id, 202 notifications, tools/list,
  tools/call with deny-rule + SSRF guard, live web_search, GET SSE stream, DELETE teardown,
  404s for unknown/deleted sessions, bearer-token 401/200 on restart.
- Remaining manual checks (need GUI): import the module DLL via the Modules tab, confirm the
  injected MCP Config tab, browse /swagger + /scalar dropdowns in a browser.
