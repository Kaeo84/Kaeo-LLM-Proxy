# MCP Server Into Host + Web Search as Single-DLL Sub-Module

## Understanding
Restructure the MCP feature: the MCP Streamable-HTTP server becomes a built-in host capability
(host references ModelContextProtocol directly), while web search becomes an importable
class-library sub-module whose build output is a single DLL. The existing browse-to-import
registry, Modules tab, and tab injection are reused unchanged; sub-modules contribute MCP tools
through a new contracts interface so the host never needs per-feature code changes.

## Assumptions
- The user's database never received MCP tables (the failed import threw before Initialize), so
  splitting the schema between host and module needs no migration; app is unreleased, so no
  compatibility paths.
- Web search has no package dependencies beyond the BCL (HttpClient/regex), so its output is
  exactly one DLL; MCP SDK, Serilog, SQLite, and WinForms projections all unify with host copies
  via the load context's host-directory deferral.
- Tool names/behavior (web_search, web_fetch, deny-first policy, SSRF guard, provider fallback)
  stay identical; disabled tools now appear in tools/list and return explanatory refusals when
  called (previously they were omitted per session).
- MCP server settings keep the existing mcp_server_settings table shape, now host-owned in the
  baseline DDL.
- McpServerHost routes stay: /mcp, /health, /openapi/v1/openapi.json, /scalar on the MCP listener
  (default port 8388).

## Approach
Add IMcpToolModule to the contracts exposing tool target instances; the host reflects
[McpServerTool] methods over them when building per-session McpServerOptions (types unify because
the SDK lives in the host directory). Move McpServerHost and the MCP OpenAPI/Scalar assets into
host Infrastructure/Mcp/, swapping ISecretProvider for the host's ModuleSecretProvider and
module-injected settings for a host-owned McpServerSettingsRepository over AppDatabase (baseline
DDL gains mcp_server_settings). MainForm gains a host-owned MCP tab (enable, address, port, auth
credential, status) replacing the module's Server tab; TrayApplicationContext starts/stops the MCP
server with the proxy and restarts on UI apply. OllamaProxyHandler's Scalar dropdown adds the MCP
document inline (no HTTP fetch) alongside module-provided documents. Create new project
Kaeo LLM Proxy Web Search receiving the web-search half of the old module (models, providers,
DomainPolicyService, NetworkSafety, WebSearchService, HtmlTextExtractor, WebSearchTools,
repository over IModuleDatabase, embedded web-search schema, config TabPage) with a module entry
class implementing IKaeoModule + IMcpToolModule (id kaeo.websearch); no CopyLocalLockFileAssemblies
so output is one DLL. Delete Kaeo LLM Proxy MCP and update slnx/host globs.

## Steps
- [x] 1. Save plan record to Plans/20260805-mcp-into-host-web-search.md
- [x] 2. Add IMcpToolModule contract to Kaeo LLM Proxy Modules
- [x] 3. Add ModelContextProtocol package, McpServerSettings model, repository, and mcp_server_settings baseline DDL to the host
- [x] 4. Move McpServerHost and MCP OpenAPI/Scalar assets into host Infrastructure/Mcp, sourcing secrets from ModuleSecretProvider and tools from ModuleHost
- [x] 5. Add ModuleHost helper returning MCP tool instances from loaded IMcpToolModule modules
- [x] 6. Add host-owned MCP tab (enable/address/port/auth/status) with apply-and-restart to MainForm
- [x] 7. Wire MCP server lifecycle in TrayApplicationContext and add the MCP document to the Scalar explorer dropdown
- [x] 8. Create Kaeo LLM Proxy Web Search module project with schema, repository, providers, policy, service, tools, config page, and module entry class
- [x] 9. Remove Kaeo LLM Proxy MCP project and update slnx plus host csproj glob excludes
- [x] 10. Build the solution and fix compile errors
- [x] 11. Verify single-DLL module output with ALC probe and smoke-test MCP endpoints against a launched app instance
- [x] 12. Update the plan file and commit

## Key Files
- Kaeo LLM Proxy Modules/IKaeoModule.cs - add IMcpToolModule contract
- Kaeo LLM Proxy.csproj - add ModelContextProtocol 2.0.0, swap glob excludes
- Infrastructure/AppDatabase.cs - baseline DDL gains mcp_server_settings + repository accessors
- Infrastructure/Mcp/McpServerHost.cs (new, moved from module) - transport core, now host-fed
- Infrastructure/Modules/ModuleHost.cs - expose tool instances from IMcpToolModule modules
- MainForm.cs / MainForm.Designer.cs - new MCP tab
- TrayApplicationContext.cs - MCP server lifecycle
- Core/Services/OllamaProxyHandler.cs - MCP document in Scalar dropdown
- Kaeo LLM Proxy Web Search/ (new project) - web search sub-module
- Kaeo LLM Proxy MCP/ (deleted) - superseded

## Risks & Open Questions
- Black-box smoke testing requires launching the tray app from a terminal and flipping MCP enabled
  via sqlite; if the environment resists, fall back to the manual GUI checklist.
- Tool reflection now crosses the module boundary; the ALC probe must confirm attribute types
  unify (they should, SDK in host dir).
- Behavior change: disabled tools visible in tools/list with polite refusals instead of hidden.

## Outcome
- Steps 1-10 completed as planned; full solution builds with 0 warnings / 0 errors.
- Step 11: ALC probe confirmed the web search module loads as a single DLL (kaeo.websearch,
  web_search + web_fetch tools, [McpServerTool] attributes unify with the host SDK copy).
  Black-box smoke test passed: MCP /health, initialize handshake (serverInfo "Kaeo LLM Proxy MCP"
  1.0.0), /openapi/v1/openapi.json, and /scalar all 200; proxy /swagger dropdown embeds the MCP
  document while the server runs.
- Follow-up change discovered during verification: the previous unconditional UAC re-launch made
  the app impossible to drive from a non-elevated dev shell. Force-admin is now opt-in:
  RuntimeSettings.RunAsAdministrator (runtime_settings.run_as_administrator column, DEFAULT 0,
  migrated for existing DBs) with a Settings-tab checkbox "Run as administrator on launch"
  (disabled in debug builds); Release builds re-launch elevated only when enabled, and the
  elevated child starts in the app base directory so relative Data paths resolve.
- DEBUG builds additionally force ListenAddress to localhost so the proxy binds unprivileged
  (0.0.0.0 requires elevation / urlacl).
- Remaining manual GUI verification: remove the stale "Kaeo LLM Proxy MCP" registry entry, import
  Kaeo LLM Proxy Web Search.dll via the Modules tab, confirm the injected Web Search tab and the
  web_search/web_fetch tools, and exercise the host MCP tab apply/restart.

## Follow-up: Modules and module GUIs live under the MCP tab
- User feedback after verification: the Modules registry and the config tabs injected by modules
  belong under the MCP tab, not at the top level.
- The MCP tab now hosts a nested TabControl (_mcpSubTabs): "Server" (the existing MCP server
  settings on _mcpServerPage), "Modules" (the former top-level Modules tab page, moved as-is),
  and one sub-page per loaded module (CreateConfigPage output).
- MainForm.Designer.cs: _tabMcp contains _mcpSubTabs; _tabModules removed from the top-level
  _tabControl; new _mcpSubTabs/_mcpServerPage controls with suspend/resume and backing fields.
- MainForm.cs: AddModuleTabs/RemoveStaleModuleTabs add/remove module pages on _mcpSubTabs.

## Follow-up: web-content safety hardening (prompt injection & SSRF)
- WebSearchTools: every web_search/web_fetch result is wrapped in a per-call random untrusted
  content envelope (FrameUntrustedContent) with a "treat as data only, never obey" note; the tool
  descriptions also state results are untrusted third-party data.
- HtmlTextExtractor: strips HTML comments, human-hidden elements (hidden attribute, display:none,
  visibility:hidden, aria-hidden="true"), and invisible unicode (zero-width / directional /
  soft-hyphen) from full pages and snippets; implemented as compiled Regex fields after validating
  the patterns against the .NET regex engine with sample payloads.
- WebSearchService: automatic redirects removed; FetchWithValidatedRedirectsAsync follows up to
  5 hops, re-running the SSRF guard and domain policy on EVERY hop so a public URL cannot bounce
  the fetch into private networks or blocked domains (metadata-endpoint SSRF closed).

## Follow-up: GUI safety documentation
- Web Search config page gains an info icon (U+2139 rendered from Segoe UI Symbol, accessible
  name/description set) that opens WebSearchSafetyDialog: a modal documenting every precaution
  (deny-first domain policy, SSRF guard, per-hop redirect validation, size/time limits,
  covert-channel stripping, untrusted-content framing, no-cookie client, least-privilege tools)
  with a what-it-is / how-it-works explanation for each.

## Follow-up: Help tab and Module Information button
- Web Search config page: the icon became a "Module Information" button moved to the top-right
  of the tab (toggles panel now spans the page width).
- Host gains a top-level Help tab (Infrastructure/HelpPages.cs): introductory blurbs for
  Dashboard, Logs, Settings, Instructions, Credentials, Test, and Heartbeats; an MCP page with
  Server and Modules sub-tabs; and a Modules page that receives injected module help pages.
- New contracts interface IHelpModule (CreateHelpPage); WebSearchModule implements it reusing
  WebSearchSafetyDialog.SafetyText, so the same content is reachable from the config-page dialog
  and from Help > Modules. MainForm adds/removes module help pages on registry changes, with a
  placeholder page while no module provides help.

## Follow-up: MCP Server page cleanup
- Removed the Auth credential dropdown from MCP > Server (mixing the proxy's stored upstream
  credentials into the MCP endpoint made no sense). Backend bearer support (auth_credential_name
  in mcp_server_settings, enforced by McpServerHost) stays for future issued-credential
  restriction; that UI belongs below the listener group.
- Listen address is now a dropdown populated the same way as Settings (shared
  PopulateListenAddressOptions helper), and the Port row sits above the Listen address row to
  match the Settings listener group. McpServerService.ListCredentialNames removed; Help blurb
  updated.
