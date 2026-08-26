# Add sqlite-vec Native Vector Search to Code Vector Store

## Summary
Replace the brute-force in-memory cosine scan in `CodeVectorDatabase.Search` with native
sqlite-vec (`vec0` virtual table) KNN search. The `vec0` native extension is loaded into the
existing `Microsoft.Data.Sqlite` connection; one `vec0` virtual table is created per
collection+dimension (`cv_vec_{colId}_{dim}`), keyed by chunk id (`rowid = chunk_id`).
Embeddings are L2-normalized in the vec table so the native L2 distance converts exactly to
cosine similarity (`cos = 1 - d²/2`). All write paths keep the vec tables in sync, and every
native path degrades gracefully to the existing brute-force search when the native library or
table is unavailable.

## Design decisions
- Use the raw `vec0` native extension via `SqliteConnection.LoadExtension("vec0")` (matches the
  module's raw-SQLite, no-DI style) instead of the `Microsoft.Extensions.VectorData` provider
  abstraction.
- csproj: swap the unused `CommunityToolkit.VectorData.SqliteVec` reference for a direct
  `sqlite-vec` (0.1.7-alpha.2.1) reference, which ships `vec0.dll`/`vec0.so`/`vec0.dylib`.
- Per-collection tables are keyed by `collection rowid + dimension` so collections with
  different embedding models/dimensions coexist; stale-dimension tables are dropped.
- Legacy collections (created before this feature) get their vec table created + backfilled
  (normalized embeddings) on first use.
- `Search` uses native KNN when: extension loaded AND table exists AND query dim matches;
  otherwise (or on native failure) falls back to the existing brute-force cosine scan.

## Steps
- [x] 1. csproj: replace `CommunityToolkit.VectorData.SqliteVec` with `sqlite-vec` 0.1.7-alpha.2.1
- [x] 2. `CodeVectorDatabase`: load `vec0` extension in constructor (graceful fallback flag)
- [x] 3. `CodeVectorDatabase`: per-collection vec0 table ensure/create/backfill helpers
- [x] 4. `CodeVectorDatabase`: keep vec tables in sync (insert/delete/collection-drop paths)
- [x] 5. `CodeVectorDatabase`: native KNN search with cosine conversion + brute-force fallback
- [x] 6. `IndexingEngine`: pass collection name into `InsertChunk`/`DeleteFileChunks`
- [x] 7. Build and fix any compilation errors
- [x] 8. Commit to git

## Follow-up (verification via Local Test MCP)
- [x] Functional test via Local Test MCP: `code_search` read path returns relevant results; `code_index`
      write path re-indexes and the file becomes the top hit (round-trip OK).
- [x] Gap found: legacy collection `Kaeo-LLM-Proxy` stored `dimension = 0`, which would keep the
      native path's `queryDim == storedDim` guard from ever engaging.
- [x] Fix: `GetOrCreateCollection` repairs stored dimension 0 from the active backend's dimension;
      `EnsureVecTable` infers the dimension from existing chunks and persists it. Committed as
      `1c3f151`.
- [ ] User action: restart the app (running Local Test instance is an older binary; the cached
      `CodeVectorDatabase` instance predates the `_vecAvailable` field). After restart, the first
      index/search repairs the dimension, creates + backfills `cv_vec_{id}_2560`, and native KNN
      engages. Confirm via log: "sqlite-vec extension loaded" and "created native vector table".
