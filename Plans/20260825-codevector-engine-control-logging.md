# Code Vector Store: Engine Diagnostics, Control, and Queue Safety

## Problem
On a server the Code Vector Store indexing engine silently stops with no diagnostic trail, cannot be
restarted, and its queue grows unboundedly (~3 GB RAM) because the mirror sync requeues every file
on each sync even when the engine is down.

## Root causes
1. `IndexingEngine.ProcessQueueAsync` — the `await foreach` over `_queue.Reader.ReadAllAsync(ct)`
   was outside the per-job try/catch, so a faulting channel read killed the worker with no log.
2. `Start()` did `if (_workers is not null) return;` — after a crash `_workers` was still non-null
   (just completed), so the engine could never restart.
3. Engine lifecycle only wrote to the MCP activity log (requires MCP server running + non-None level);
   the System Logs tab (Serilog) got nothing.
4. `EnqueueIndexFile` always enqueued; the mirror sync requeued all files each interval, so with the
   engine down the unbounded channel + `_pendingJobs` grew every sync.

## Changes
- [x] **IndexingEngine.cs**
  - Worker loop now catches ALL exceptions (per-job + channel read), logs to Serilog + activity, and
	records a `StopReason`.
  - `Start()` is restartable (lifecycle lock, fresh CTS if cancelled, `??=` semaphore).
  - Added `StopAsync(string? reason)`, `ClearQueue()`, and a `StopReason` property.
  - `EnqueueIndexFile` returns bool, supports `onlyIfChanged`, and skips when the engine is not running.
  - Start/stop/crash/clear events log to Serilog (System Logs tab).
- [x] **CodeVectorModule.cs**
  - Added `StartEngine()`, `StopEngineAsync(reason)`, and `EngineStopReason`.
  - Whole-module `StopAsync` passes a reason; engine-level stop does NOT dispose backend/DB.
- [x] **GitMirrorManager.cs**
  - Sync queues only changed files (`onlyIfChanged: true`) and nothing when the engine is stopped.
- [x] **CodeVectorConfigPage.cs**
  - Added Start Engine / Stop Engine / Clear Queue buttons, a stop-reason tooltip, and button-state
	updates in the Status group.
- [x] **CodeVectorTools.cs**
  - `CodeIndex` reflects the actual enqueue result (reports when the engine is not running).

## Notes / decisions
- "Main Logs tab" = the System Logs sub-tab (Serilog-backed). Engine diagnostics route there via
  Serilog `Log` (shared with the host — the module ALC defers Serilog to the default context). No new
  tab added; the config page's existing activity log keeps per-file detail.
- Engine-level start/stop controls only the workers; the mirror sync timer keeps running but skips
  enqueuing while the engine is down (keeps mirrors fresh, no queue growth).
- `ClearQueue` is best-effort (a concurrently enqueued job may be dropped from the snapshot or
  processed without showing).
