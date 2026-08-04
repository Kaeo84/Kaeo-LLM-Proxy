# Clear Logs button deletes logs from the database

## Problem

The Clear button on the Logs tab only resets in-memory state (`StatisticsService.Reset()` plus the
`MainForm._logCache` ListView cache). The persisted rows in the SQLite `requests` table (and linked
`exceptions` rows) remain, so logs survive a Clear and are re-seeded into the view on the next startup.

## Approach

- `AppDatabase.ClearLogs()` — delete all rows from `requests` and `exceptions` in one transaction, and
  reset the AUTOINCREMENT counters via `sqlite_sequence` (SQLite has no TRUNCATE; this is the equivalent).
- `StatisticsService.ClearLogs()` — reset the in-memory queue, counters, and snapshot cache (reuses
  `Reset()`), then route the database wipe through the bounded persistence channel using a sentinel
  entry so it executes after all still-queued entries have been written; otherwise in-flight entries
  would land in the DB after the wipe and reappear.
- `MainForm.BtnClearLogs_Click` — call `_stats.ClearLogs()` instead of `_stats.Reset()`; the view and
  local cache clearing stays as-is.

## Steps

- [x] Add `ClearLogs()` to `Infrastructure/AppDatabase.cs`
- [x] Add `PersistEntry.ClearLogs` sentinel + writer handling in `Core/Services/StatisticsService.cs`
- [x] Add `StatisticsService.ClearLogs()`
- [x] Update `MainForm.BtnClearLogs_Click` to use `ClearLogs()`
- [x] Build and verify (compile clean; output copy blocked only by the running app instance locking the exe)
- [x] Git commit
