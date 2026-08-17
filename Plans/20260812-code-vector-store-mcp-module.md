# Code Vector Store MCP Module

## Summary
Create a new MCP module that provides embeddings and a vector store for code. It exposes tools for indexing, searching, and syncing git mirrors so AI clients can query relevant code chunks instead of sending thousands of lines.

## Decisions
- Sync: both agent-push and server-side git mirrors (LibGit2Sharp)
- Backends: remote/proxied first, ONNX CPU next, GGUF later
- Storage: module-owned SQLite DB in the same data directory as the main app DB
- Single-file module (CodeVectorModule.cs)

## Steps
1. Add ModuleContext.DataDirectory and expose AppDatabase.DatabasePath
2. Create project and single-file module with shared-settings schema and MCP tool stubs
3. Wire module into the host and verify build
