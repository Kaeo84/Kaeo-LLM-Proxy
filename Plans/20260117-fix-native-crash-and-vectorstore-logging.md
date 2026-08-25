# Fix LibGit2Sharp Native Type-Initializer Crash + Vector Store Error Logging

## Status: COMPLETED (commit a83952d)

## Understanding
Sync Failed dialog showed "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception", and nothing about the error appeared in any log.

## Root Causes
1. **NativeMethods crash**: The module pre-registered `NativeLibrary.SetDllImportResolver` on the LibGit2Sharp assembly. LibGit2Sharp 0.30's own `NativeMethods` static constructor also calls `SetDllImportResolver`; a pre-existing registration makes that initializer throw `InvalidOperationException` → TypeInitializationException.
2. **No logs**: `GitMirrorManager.SyncMirrorAsync` catch filter was `IOException or LibGit2SharpException`, so `TypeInitializationException` escaped unlogged to the UI; UI handlers only showed MessageBoxes; `CodeVectorActivityLogger` dropped even error entries when level = None.

## Changes
- [x] `CodeVectorModule.EnsureLibGit2NativeLibraryAvailable`: removed resolver pre-registration + reflection; set `LibGit2Sharp.GlobalSettings.NativeLibraryPath = nativeDir` (supported mechanism; directory; LibGit2Sharp appends git2 file name) before first native use.
- [x] `GitMirrorManager.SyncMirrorAsync`: catch all (except cancellation), log full exception (`{ex}`) to MCP activity log, record failed status, rethrow.
- [x] `CodeVectorActivityLogger.Log`: errors always written to MCP log regardless of level.
- [x] `CodeVectorConfigPage`: Sync/Index/Reindex/Edit catch blocks now log to activity log before showing dialogs.
- [x] `CodeVectorTools`: search/index/sync catch blocks log to activity log.

## Vector Store Log Tab
Not needed: module activity already flows to host **Logs → MCP sub-tab** via `McpActivityLogAdapter` (Method column = "CodeVector", red rows on error, full exception in the details view). The gap was that errors were never written, now fixed.

## Verification
- [x] Full solution Release build: 0 errors
- [x] Module DLL still embeds moduledep/LibGit2Sharp.dll, moduledep/git2-a418d9d.dll, moduledep/WinRT.Runtime.dll (2.9MB)
- [x] Committed: a83952d
