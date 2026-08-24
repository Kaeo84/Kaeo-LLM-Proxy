# 2026-08-24: Code Vector Store Module Folder Split

## Goal
Break the single 2548-line `Kaeo LLM Proxy Module Code Vector Store\CodeVectorModule.cs` into logical
folders within the module (still one class library), mirroring the solution-wide Core/Infrastructure split.
`CodeVectorModule.cs` stays in the module root, stripped down to startup/lifecycle wiring only
(like `Program.cs` in the main app).

## Target layout
```
Kaeo LLM Proxy Module Code Vector Store\
  CodeVectorModule.cs              (slim: lifecycle wiring only)
  Core\
	BackendType.cs                 (enum)
	CodeVectorMcpLogLevel.cs       (enum)
	CodeVectorSettings.cs
	CodeVectorRepository.cs        (+ moved static ApplySharedSchema/SharedSchema)
	MirrorRegistration.cs
	CodeVectorDatabase.cs
	CollectionInfo.cs
	SearchResult.cs
	CodeChunk.cs
	CodeChunker.cs
	IEmbeddingBackend.cs
	RemoteEmbeddingBackend.cs
	OnnxEmbeddingBackend.cs
	WordPieceTokenizer.cs
	IndexingEngine.cs              (keeps nested JobType, IndexJob, QueueItemInfo)
	GitMirrorManager.cs
	VectorSearchEngine.cs
	EmbeddingBackendFactory.cs     (new: moved from module)
  Infrastructure\
	CodeVectorActivityLogger.cs    (keeps nested LogEntry)
  Mcp\
	CodeVectorTools.cs
  UI\
	CodeVectorConfigPage.cs
	RepoDialog.cs
	ModelInfoDialog.cs
	CodeVectorHelpPage.cs          (new: moved from module)
```

## Decisions
- All types keep namespace `Kaeo.LlmProxy.Module.CodeVector` (all internal, module-local only).
- Nested types travel with their parent class.
- Slimming the module moves out:
  - `SharedSchema` + `ApplySharedSchema` -> `CodeVectorRepository` (internal static; `Initialize` calls `CodeVectorRepository.ApplySharedSchema(...)`).
  - `CreateEmbeddingBackend` -> `Core/EmbeddingBackendFactory.Create(...)` (callers: `Initialize`, `InvalidateEmbeddingBackend`).
  - `CreateHelpPage` + `HelpText` -> `UI/CodeVectorHelpPage.Create()` (module method becomes one-liner).
- Per-file `using` lists computed by keyword matching from the original 15 usings; implicit usings cover System.*, System.IO, System.Linq, System.Net.Http, System.Threading(.Tasks), System.Drawing, System.Windows.Forms.

## Steps
- [x] Save plan file to Plans/ folder
- [x] Extract 21 type segments into Core/, Infrastructure/, Mcp/, UI/ folders via PowerShell script
- [x] Create Core/EmbeddingBackendFactory.cs and UI/CodeVectorHelpPage.cs
- [x] Add shared schema members to Core/CodeVectorRepository.cs
- [x] Slim CodeVectorModule.cs (remove other types, moved members, update call sites and usings)
- [x] Build module project and fix errors
- [x] Update plan file checkboxes and git commit

## Result
- `CodeVectorModule.cs`: 2548 -> 201 lines (lifecycle wiring only).
- Module project builds successfully.
- Note: root host project build fails due to pre-existing untracked stale folders
  (`Kaeo LLM Proxy Code Vector Store`, `Kaeo LLM Proxy Modules`, `Kaeo LLM Proxy SSH`,
  `Kaeo LLM Proxy Web Search` obj dirs polluting the root project's default glob) — unrelated to this change.
