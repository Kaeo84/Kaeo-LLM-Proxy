# Complete MCP tool metadata for the Code Vector Store module

## Understanding
The Code Vector Store module's MCP tools expose only terse one-line descriptions and rely on auto-derived tool names, so the `tools/list` JSON lacks the rich, actionable metadata that the Web Search and SSH modules already provide. Goal: make every tool fully self-describing (explicit snake_case name, rich tool description, complete input schema with per-property descriptions and correct required/optional marking), refresh stale host-level MCP descriptions, and document the tool-authoring contract in copilot-instructions so future modules follow the same standard.

## Assumptions
- The host builds `tools/list` JSON via `McpServerTool.Create` reflection (McpServerOptionsFactory), so all metadata flows from `[McpServerTool(Name=...)]` and `[Description]` attributes; required parameters are those without default values.
- Web Search and SSH modules already meet the standard and need no changes.
- The authoritative copilot-instructions file is `.github/copilot-instructions.md` (the IDE-loaded one); the root `copilot-instructions.md` is stale legacy and left untouched.

## Approach
Rewrite the attribute metadata in `Kaeo LLM Proxy Module Code Vector Store/Mcp/CodeVectorTools.cs`: add `[McpServerToolType]`, explicit snake_case names, rich descriptions (when to call, what it does, what it returns, caveats, relation to sibling tools), and complete parameter descriptions with defaults/units/optionality. Make `remoteUrl` optional in `code_sync_repo` with validation, since it is ignored when `localDirectory` is set (fixes an incorrect `required` entry). Update the tool list in `CodeVectorHelpPage.cs` (missing `code_list_collections`). Fix the stale server description in `McpApiExplorer.cs` ("Exposes the web_search and web_fetch tools") and enrich `ServerInstructions` in `McpServerOptionsFactory.cs`. Add an "MCP Module Tool Authoring" section to `.github/copilot-instructions.md` with the reference JSON template and attribute-mapping rules.

## Key Files
- Kaeo LLM Proxy Module Code Vector Store/Mcp/CodeVectorTools.cs — the 7 tools needing complete metadata
- Kaeo LLM Proxy Module Code Vector Store/UI/CodeVectorHelpPage.cs — stale tool list
- Kaeo LLM Proxy Services/Mcp/McpApiExplorer.cs — stale OpenAPI info description
- Kaeo LLM Proxy Infrastructure/Mcp/McpServerOptionsFactory.cs — server instructions
- .github/copilot-instructions.md — tool-authoring contract for future modules

## Steps
- [x] 1. Rewrite CodeVectorTools tool metadata — names, tool descriptions, parameter descriptions, optional remoteUrl with validation
- [x] 2. Update CodeVectorHelpPage tool list — include all 7 tools with one-line descriptions
- [x] 3. Update host server-level MCP descriptions — McpServerOptionsFactory instructions and McpApiExplorer OpenAPI info text
- [x] 4. Document MCP tool authoring contract in .github/copilot-instructions.md
- [x] 5. Build the solution and verify no errors
- [x] 6. Commit changes to git
