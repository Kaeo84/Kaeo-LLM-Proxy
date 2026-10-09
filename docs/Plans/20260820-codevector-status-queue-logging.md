# Code Vector Store — Status View, Queue List & Full Logging

## Understanding
The Code Vector Store config page needed a live status panel, a queue list view, and full
activity logging. Additionally, a root-cause bug prevented mirror-synced files from being
indexed: `IndexMirrorFilesAsync` used `repo.RetrieveStatus()`, which only returns files with a
non-current status. After the initial clone, all files are "current", so subsequent syncs
discovered nothing to index.

## Changes (all in `Kaeo LLM Proxy Code Vector Store/CodeVectorModule.cs`)
1. **Mirror file discovery fix** — replaced `RetrieveStatus` with a recursive `WalkTree` over
   `repo.Head.Tip.Tree` so every tracked file is enumerated regardless of status.
2. **Queue + current-job tracking** — `IndexingEngine` now maintains a `ConcurrentQueue<QueueItemInfo>`
   of pending jobs, a volatile `CurrentJob`, `GetQueueSnapshot()`, and accurate `QueueDepth`.
3. **Full activity logging** — `CodeVectorActivityLogger` gained a 500-entry in-memory ring buffer,
   `TotalLogged`/`ErrorCount` counters, `GetRecentEntries()`, `ClearBuffer()`, and an `Activity`
   accessor on the module. Detailed events added: `file_start`, `embed_batch`, `file_complete`,
   `sync_start`, `sync_complete`, `sync_success`, plus per-file skip reasons.
4. **Status UI** — new "Status" GroupBox on the config page with engine/queue/current labels,
   a log summary, a queue ListView, and an activity log ListView with a Clear button.
5. **Auto-refresh** — a 2s `System.Windows.Forms.Timer` refreshes the status; disposed on form close.

## Steps
- [x] Fix `IndexMirrorFilesAsync` — replace `RetrieveStatus` with tree walk to get all tracked files
- [x] Add queue snapshot + current job tracking to `IndexingEngine`
- [x] Enhance `CodeVectorActivityLogger` with ring buffer and detailed log events
- [x] Add detailed logging calls throughout `IndexingEngine` and `GitMirrorManager`
- [x] Build the Status GroupBox UI (status labels, queue ListView, log ListView, refresh timer)
- [x] Wire up auto-refresh and cleanup (timer disposal on form close)
- [x] Build and verify no compilation errors
