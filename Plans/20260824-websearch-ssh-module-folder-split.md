# 2026-08-24: Web Search + SSH Module Folder Split

## Goal
Break the single-file `WebSearchModule.cs` (1959 lines) and `SshModule.cs` (2219 lines) into logical
folders within each module (still one class library each), mirroring the Code Vector Store module split.
The `<Module>.cs` file stays in the module root, stripped down to startup/lifecycle wiring only.

## Target layout
```
Kaeo LLM Proxy Module Web Search\
  WebSearchModule.cs               (slim: 1959 -> 116 lines, lifecycle wiring only)
  Core\
	DomainRuleType.cs              (enum)
	DomainRule.cs
	SearchProviderConfig.cs
	WebSearchSettings.cs
	SearchResult.cs                (record)
	ISearchProvider.cs             (interface)
	DuckDuckGoSearchProvider.cs
	SearXngSearchProvider.cs
	BraveSearchProvider.cs
	BingSearchProvider.cs
	DomainPolicyService.cs
	NetworkSafety.cs               (static)
	HtmlTextExtractor.cs           (static partial)
	WebSearchRepository.cs
	WebSearchService.cs
  Mcp\
	WebSearchTools.cs
  UI\
	WebSearchConfigPage.cs
	WebSearchSafetyDialog.cs
	ProviderConfigDialog.cs
	TextPromptDialog.cs

Kaeo LLM Proxy Module SSH\
  SshModule.cs                     (slim: 2219 -> 191 lines, lifecycle wiring only)
  Core\
	SshStoredConnection.cs
	SshMcpLogLevel.cs              (enum)
	SshSettings.cs
	SshConnectionRequest.cs
	SshCommandResult.cs
	OpenSshConnectionInfo.cs
	SshRepository.cs
	SshConnectionManager.cs        (keeps nested ManagedConnection)
  Infrastructure\
	SshActivityLogger.cs
  Mcp\
	SshTools.cs
  UI\
	SshConfigPage.cs
	SshConnectionDialog.cs
```

## Decisions
- All types keep their namespace (`Kaeo.LlmProxy.Module.WebSearch` / `Kaeo.LlmProxy.Module.Ssh`); all internal, module-local.
- Nested types travel with their parent (e.g. `ManagedConnection` stays in `SshConnectionManager.cs`).
- Folder convention mirrors the Code Vector Store module: Core = domain models, enums, settings,
  records, interfaces, services, providers, repository, managers; Infrastructure = activity logger only;
  Mcp = MCP tool classes; UI = WinForms pages/dialogs; root = the module entry-point class.
- Moved-out files carry the full original `using` block (unused usings are harmless; no TreatWarningsAsErrors).
- Slimmed root files get a minimal using set: `Kaeo.LlmProxy.Core.Modules` + `ModelContextProtocol.Server`.
- No behavior changes; the split moved code verbatim.

## Steps
- [x] Write the split script (Split-Module function)
- [x] Run the split for the Web Search module
- [x] Verify Web Search folder layout and slimmed root file
- [x] Run the split for the SSH module
- [x] Verify SSH folder layout and slimmed root file
- [x] Build both module projects and fix errors
- [x] Save plan file to Plans/ folder and git commit

## Result
- `WebSearchModule.cs`: 1959 -> 116 lines (lifecycle wiring only); 20 types moved to Core/Mcp/UI.
- `SshModule.cs`: 2219 -> 191 lines (lifecycle wiring only); 12 types moved to Core/Infrastructure/Mcp/UI.
- Both module projects build with 0 errors.
- Note: the SSH module emits one pre-existing CS8604 warning (SshTools.cs, `ConnectAsync(request, ...)`)
  that exists identically in the original file; the split preserved the code verbatim and did not introduce it.
