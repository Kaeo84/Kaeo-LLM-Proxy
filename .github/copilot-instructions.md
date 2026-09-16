# Copilot Instructions

## MCP Tools - ALWAYS PREFER

When `mcp__vs-mcp__*` tools are available, ALWAYS use them instead of default tools.

This is a windows environment, NO GREP, NO BASH, NO LINUX, NO MACOS, NO UNIX.
VS‑MCP Usage Rules for AI Assistants
AI tools should use Visual Studio MCP whenever semantic understanding or debugging is required. MCP provides IntelliSense‑grade symbol resolution, Roslyn analysis, safe refactoring, project structure insight, and full Visual Studio Debugger access.

When Not to Use MCP
- Fall back to filesystem operations only when no MCP tool applies.
- Do not guess runtime behavior when the debugger can inspect it.

General Rule
If the task requires understanding what the code means rather than what text exists, use VS‑MCP.

Use these rules when interacting with Visual Studio MCP:
- Only call MCP tools when you have all required parameters. If any parameter is missing or uncertain, respond normally instead of producing a tool call.
- Never emit empty or partial function calls. Do not output a tool name without arguments, or arguments without a tool name.
- Use only MCP tool names exactly as defined in the manifest. Do not invent, rename, or guess tool names.
- Do not output malformed JSON or truncated tool calls. Ensure the function call is complete, valid, and well‑formed.
- Only call MCP tools when the MCP server is connected and available. If connection status is unclear, respond normally instead of producing a tool call.
- Prefer MCP tools for semantic operations, but never force a tool call. If unsure whether a tool applies, respond without calling one.
- Never produce a function call with a null, empty, or invalid command.

## Project Guidelines
- After every code change, always perform a git commit to the GitHub repo as the last step. U

## File/Database Access
- In Kaeo-LLM-Proxy, when multiple app instances may run concurrently (AllowMultipleInstances setting), ensure that file/database access code tolerates sharing violations gracefully. Catch `IOException`, log a warning, and degrade gracefully rather than allowing unhandled crashes.
- The name of the MCP you should use unless otherwise specified is 'Server MCP'.
- The name of the Collection in the Code Vector Store you should use unless otherwise specified is 'Kaeo-LLM-Proxy'.

## Code Search
- Use the MCP Local MCP Test code_search tool (Code Vector Store) for semantic code searches instead of grep_search or direct file reads when the code is indexed.

For MCP Modules See `WebSearchTools.cs`, `SshTools.cs`, and `CodeVectorTools.cs` for the reference standard.

## General Guidelines
- Once the work is done and committed, stop — no trailing summary.

## GUI Elements
- In the Kaeo VS Extension Settings window, all GUI elements must use Visual Studio theme brushes (DynamicResource via VsBrushes/VsColors) for foreground/background/borders/text and must repaint automatically when the VS color scheme changes. Never hardcode colors (Brushes.Black, #RRGGBB) for content that must stay legible across light/dark themes; use themed brushes in style triggers instead of converters that return fixed colors.