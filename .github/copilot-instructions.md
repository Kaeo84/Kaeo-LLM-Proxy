# Copilot Instructions

## Project Guidelines
- After every code change, always perform a git commit to the GitHub repo as the last step. The commit message should include a short summary of what the change was for and what was changed.
- For this unreleased/new app, do not add migration/update compatibility paths or unnecessary migration code unless explicitly requested.
- Keep module-specific dependencies isolated to their owning module; do not add module-only packages such as LibGit2Sharp to the main Proxy project. The Proxy host should remain standalone, with the Vector Store module shipping or resolving its own Git dependency.

## File/Database Access
- In Kaeo-LLM-Proxy, when multiple app instances may run concurrently (AllowMultipleInstances setting), ensure that file/database access code tolerates sharing violations gracefully. Catch `IOException`, log a warning, and degrade gracefully rather than allowing unhandled crashes.

## Code Search
- Use the MCP Local MCP Test code_search tool (Code Vector Store) for semantic code searches instead of grep_search or direct file reads when the code is indexed.

## MCP Module Tool Authoring
Modules that contribute tools to the host MCP server (via `IMcpToolModule.CreateMcpToolTargets`) must make every tool fully self-describing. Clients only ever see the `tools/list` JSON, which the host generates from the ModelContextProtocol attributes — everything an agent needs to call a tool correctly must be contained in it.

Every tool must produce JSON covering all of these properties:

```json
[
  {
    "name": "sync_file_to_vector_store",
    "description": "Call this whenever a file is created or modified. It chunks the code, generates embeddings, and updates the vector store.",
    "input_schema": {
      "type": "object",
      "properties": {
        "file_path": { "type": "string", "description": "Relative path of the modified code file." },
        "commit_hash": { "type": "string", "description": "Optional current Git SHA to track state sync." }
      },
      "required": ["file_path"]
    }
  }
]
```

How each property is provided from C#:
- `name`: set explicitly with `[McpServerTool(Name = "snake_case_name")]`; never rely on auto-derived names.
- `description`: a `[Description]` attribute on the method, written as actionable guidance — when to call the tool, what it does internally, what it returns, and how it relates to sibling tools. Destructive or dangerous tools must say so explicitly.
- `input_schema.properties`: one parameter per property; every parameter needs a `[Description]` covering meaning, format/example, units, and optionality. `CancellationToken` parameters are excluded from the schema automatically.
- `input_schema.required`: a parameter WITHOUT a default value becomes required; optional parameters are nullable with a `null` default and must describe their default behavior. Never mark a parameter required when it can legitimately be omitted.
- Mark tool classes with `[McpServerToolType]` and give them a summary comment.

See `WebSearchTools.cs`, `SshTools.cs`, and `CodeVectorTools.cs` for the reference standard.