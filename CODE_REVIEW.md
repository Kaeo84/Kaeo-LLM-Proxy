# Code Review — Kaeo LLM Proxy

**Date:** 2026-07-14  
**Scope:** Full solution review (all `.cs` source files)  
**Reviewer:** GitHub Copilot

---

## Critical Bugs

### 1. [RESOLVED] Double `AppDatabase` instantiation — SQLite locking risk
**Files:** `Program.cs` (line 29), `TrayApplicationContext.cs` (line 32)

`Program.Main` creates an `AppDatabase`, loads runtime settings, then passes `settings` into `TrayApplicationContext`, which creates a **second** `AppDatabase` on the same file. The first instance stays alive (via `using`) until `Application.Run` returns, so **two SQLite connections to the same file exist for the entire app lifetime**. Under concurrent writes (request logging + settings saves), this can cause `SQLITE_BUSY` errors or data corruption.

```csharp
// Program.cs
using AppDatabase database = new(settings.Logging);       // instance 1
settings.ApplyRuntimeSettings(database.LoadRuntimeSettings());
Application.Run(new TrayApplicationContext(settings));     // creates instance 2 inside
```

**Fix:** Remove the `AppDatabase` from `Program.cs` and let `TrayApplicationContext` own it exclusively, or pass the single instance into `TrayApplicationContext`.

---

### 2. [RESOLVED] `OllamaProxyHandler` and `StatisticsService` never disposed
**File:** `TrayApplicationContext.cs` (lines 241–257)

`Dispose(bool)` disposes `_trayIcon`, `_server`, `_database`, and `_perfService`, but **not** `_handler` (which holds an `HttpClient` and heartbeat timers) or `_stats` (which holds a cleanup `Timer`). On shutdown, heartbeat monitors keep firing and the `HttpClient` handler leaks sockets.

```csharp
protected override void Dispose(bool disposing)
{
	if (disposing)
	{
		_trayIcon.Dispose();
		_server.Dispose();
		_database.Dispose();
		_perfService.Dispose();
		// ❌ _handler.Dispose() missing
		// ❌ _stats.Dispose() missing
		AppLogger.Shutdown();
	}
}
```

**Fix:** Add `_handler.Dispose()` and `_stats.Dispose()` before `AppLogger.Shutdown()`.

---

### 3. [RESOLVED] `OnExit` — unhandled exception in `async void` crashes the process
**File:** `TrayApplicationContext.cs` (lines 233–239)

```csharp
private async void OnExit(object? sender, EventArgs e)
{
	Log.Information("Kaeo LLM Proxy shutting down");
	_trayIcon.Visible = false;
	await _server.StopAsync();   // ← if this throws, process crashes
	Application.Exit();
}
```

No `try/catch`. Per the project's own WinForms guidelines, `async void` handlers **must** wrap `await` calls in `try/catch`.

**Fix:** Wrap in `try/catch`, log the error, and still call `Application.Exit()`.

---

### 4. [RESOLVED] `UpdateSettings` — race condition on `_httpClient` swap
**File:** `OllamaProxyHandler.cs` (lines 40–46)

```csharp
public void UpdateSettings(AppSettings settings)
{
	_settings = settings;
	HttpClient old = _httpClient;
	_httpClient = BuildHttpClient(settings);
	old.Dispose();               // ← in-flight requests on 'old' will fault
	SynchronizeHeartbeatMonitors();
}
```

In-flight requests holding a reference to the old `HttpClient` will get `ObjectDisposedException` when the old client is disposed. Additionally, `_settings` is a plain field read from multiple threads without `volatile` or a lock, so request-handling threads may see a stale reference.

**Fix:** Either (a) don't dispose the old client immediately — let it drain, or (b) use a lock/`SemaphoreSlim` around the swap and mark `_settings` as `volatile`.

---

### 5. [RESOLVED] `PruneExpired` — race condition loses log entries
**File:** `StatisticsService.cs` (lines 256–262)

```csharp
RequestLog[] kept = [.. _logs.Where(r => r.Timestamp >= cutoff)];
while (_logs.TryDequeue(out _)) { }   // ← concurrent AddLog enqueues here
foreach (RequestLog entry in kept)
	_logs.Enqueue(entry);             // ← those new entries are lost
```

Between draining the queue and re-enqueuing the kept entries, concurrent `AddLog` calls can enqueue entries that get discarded by the drain loop.

**Fix:** Use a lock around the rebuild, or accept the in-memory queue as eventually consistent and only prune the SQLite store.

---

## Moderate Issues

### 6. [RESOLVED] `IsContextOverflowErrorAsync` consumes the response body
**File:** `OllamaProxyHandler.cs` (lines 211–242)

Called from `HandleChatAsync` (line 1719) with `HttpCompletionOption.ResponseHeadersRead`. `ReadAsStringAsync` buffers the content, so a subsequent `ReadAsStringAsync` in the error-forwarding path (line 553) still works. However, this **double-reads** the body into memory. For very large error responses this doubles allocation. Consider reading once and passing the string forward.

---

### 7. [RESOLVED] `AppSettings.Load` — silent catch-all swallows config errors
**File:** `AppSettings.cs` (lines 365–373)

```csharp
catch
{
	return new AppSettings();   // user loses all settings with no warning
}
```

A corrupted `settings.jsonc` silently resets to defaults. The user has no indication their configuration was lost.

**Fix:** Log the exception via `Serilog` before returning defaults.

---

### 8. [RESOLVED] `AppSettings.Save` — no error handling
**File:** `AppSettings.cs` (lines 376–381)

`File.WriteAllText` can throw (`IOException`, `UnauthorizedAccessException`). No `try/catch` means a failed save propagates up and may crash the settings-save UI flow.

---

### 9. [RESOLVED] `AppDatabase.DeleteOlderThan` — N+1 delete for exceptions
**File:** `AppDatabase.cs` (lines 659–668)

Each exception ID is deleted with a separate `DELETE` command inside a loop. For large pruning operations this is slow.

**Fix:** Use a single `DELETE FROM exceptions WHERE id IN (…)` or a temp-table join.

---

### 10. [RESOLVED] No index on `requests.timestamp_utc` (already fixed prior to this work)
**File:** `AppDatabase.cs` (schema, lines 736–759)

`LoadRecent` and `DeleteOlderThan` both filter on `timestamp_utc`. Without an index, these are full table scans. As the table grows (72-hour retention with high traffic), performance degrades.

**Fix:** Add `CREATE INDEX IF NOT EXISTS idx_requests_timestamp ON requests(timestamp_utc);`.

---

### 11. [RESOLVED] `ProxyServer.Dispose` doesn't await the listen task
**File:** `ProxyServer.cs` (lines 158–176)

`Dispose` cancels the CTS and closes the listener but never awaits `_listenTask`. The accept loop may still be running when Dispose returns, potentially accessing disposed objects.

---

### 12. [RESOLVED] `ProxyServer.AcceptLoopAsync` — `_listener!` null-forgiving operator
**File:** `ProxyServer.cs` (line 110)

If `StopAsync` runs concurrently and nulls `_listener`, the `!` suppresses the compiler warning but doesn't prevent a `NullReferenceException`.

---

### 13. [RESOLVED] `IsStreamingJsonBody` parses the entire JSON body just to check `"stream"`
**File:** `OllamaProxyHandler.cs` (lines 666–678)

For large request bodies (long conversations), `JsonDocument.Parse` allocates the full DOM just to read one boolean. A lightweight regex or `Utf8JsonReader` scan would be far cheaper.

---

### 14. [RESOLVED] `HandlePsAsync` — unnecessary `await Task.CompletedTask`
**File:** `OllamaProxyHandler.cs` (line 1340)

```csharp
await Task.CompletedTask;   // no-op
```

Dead code. Remove it.

---

### 15. [RESOLVED] `RedactSensitiveJsonFields` redacts "content", "messages", "prompt"
**File:** `OllamaProxyHandler.cs` (lines 1274–1289)

When `RedactSensitiveJsonFields` is true but `RedactRequestBodies` is false, the actual LLM prompt/response content is replaced with `[REDACTED]`. This makes the log useless for debugging the very thing the user opted to collect. Consider limiting redaction to truly sensitive fields (keys, tokens, passwords) and leaving content fields intact.

**Fix:** `IsSensitiveJsonProperty` now only matches credentials/secrets (authorization, api_key/apikey, access_token, token, secret, password); content fields (`prompt`, `system`, `messages`, `input`, `content`) are left intact. Doc comment on `ModelMapping.RedactSensitiveJsonFields` updated to match.

---

## Minor Issues / Code Smells

### 16. [RESOLVED] Indentation error in `HandleChatAsync`
**File:** `OllamaProxyHandler.cs` (line 1650)

```csharp
		log.Streaming = ollamaReq.Stream;
			var (chatBase, chatTimeout, chatApiKey) = ResolveUpstream(ollamaReq.Model);
```

Extra indentation on the `var` line.

**Fix:** Indentation corrected.

---

### 17. [RESOLVED] `BuildHttpClient` accepts an unused parameter
**File:** `OllamaProxyHandler.cs` (line 278)

```csharp
private static HttpClient BuildHttpClient(AppSettings _) =>
```

The `settings` parameter is discarded. Either use it (e.g., to configure `MaxConnectionsPerServer` from settings) or remove the parameter.

**Fix:** Already resolved in code — `BuildHttpClient()` no longer takes a parameter.

---

### 18. [RESOLVED] `ModelMappingDialog.cs` — explicit `using` directives
**File:** `ModelMappingDialog.cs` (lines 1–9)

This file has explicit `using System;`, `using System.Collections.Generic;`, etc., while the rest of the codebase relies on global usings. Inconsistent style.

**Fix:** Removed the redundant directives covered by implicit/global usings; kept only `System.Text`, `System.Text.Json`, and project namespaces.

---

### 19. [RESOLVED] `PerformanceService.Sample` — empty catch-all
**File:** `PerformanceService.cs` (lines 54–57)

```csharp
catch
{
	// Non-fatal: sampling can fail if the process is exiting.
}
```

Swallows all exceptions silently. At minimum, log at `Debug` level.

**Fix:** Catch now captures the exception and logs it at `Debug` level via Serilog.

---

### 20. [RESOLVED] `MainForm` test console creates a new `HttpClient` per request
**File:** `MainForm.cs` (line 1513)

```csharp
using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
```

While this is the test console (not the hot proxy path), it still causes socket churn on repeated test sends. Consider reusing a shared client.

**Fix:** The test console now uses a shared static `HttpClient` (`_testConsoleClient`). Sibling case `ModelMappingDialog.FetchUpstreamModelsAsync` also switched to a shared static client (`_modelFetchClient`, 10 s timeout).

---

### 21. [RESOLVED] `AppDatabase` — `PRAGMA journal_mode = WAL` set on every connection open
**File:** `AppDatabase.cs` (line 721)

WAL mode is persistent once set on the database file. Setting it on every `InitializeDatabase` call is harmless but redundant.

**Fix:** WAL setup moved out of the schema batch into `EnsureWalJournalMode`, which queries the current journal mode and only sets WAL when it differs. Failures (e.g., sharing violations from a concurrent instance) are logged and tolerated.

---

### 22. [RESOLVED] `StatisticsService.AddLog` — soft race on `_logs.Count > _maxEntries`
**File:** `StatisticsService.cs` (lines 97–98)

`ConcurrentQueue.Count` is a snapshot. Between the check and `TryDequeue`, another thread may have already dequeued, causing one extra entry to be removed. Not critical for a soft cap, but worth noting.

**Fix:** Accepted by design (lock-free hot path; SQLite store stays authoritative) and documented with an explanatory comment at the trim loop.

---

### 23. [RESOLVED] `ProxyServer.StopAsync` — calls both `Stop()` and `Close()`
**File:** `ProxyServer.cs` (lines 86–87)

`Close()` already stops the listener. Calling `Stop()` first is redundant.

**Fix:** Removed the redundant `Stop()` call; `StopAsync` now only calls `Close()`, consistent with `Dispose`.

---

## Good Practices / Positives

| Area | Detail |
|---|---|
| **Pooled HttpClient** | `OllamaProxyHandler` uses a single `SocketsHttpHandler`-backed `HttpClient` with `PooledConnectionLifetime` and `MaxConnectionsPerServer`, avoiding socket exhaustion. |
| **Per-request timeout via linked CTS** | `SendUpstreamAsync` correctly uses `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter` instead of `HttpClient.Timeout`, allowing per-mapping timeouts. |
| **Thread-safe statistics** | `StatisticsService` uses `Interlocked` operations and `ConcurrentDictionary` throughout, avoiding locks on the hot path. |
| **Heartbeat keep-alive** | The pre-response heartbeat pump (`PumpPreResponseHeartbeatsAsync`) and SSE heartbeat injection (`CopyStreamWithSseHeartbeatsAsync`) correctly keep client connections alive during long thinking phases. |
| **Proper SSE comment frames** | Heartbeats use `: kaeo-heartbeat\n\n` (SSE comment syntax), which compliant SSE clients ignore — no data corruption. |
| **`UpstreamUriHelper`** | Centralizes the tricky base-URL + relative-path combination logic, correctly handling the `/v1` deduplication problem. Well-documented with `<remarks>`. |
| **Exception detail separation** | Full stack traces are stored in a separate `exceptions` table linked by ID, keeping the `requests` table lean. |
| **WAL journal mode** | SQLite is configured with WAL, allowing concurrent readers during writes — appropriate for a proxy that logs while serving. |
| **Defensive `ProxyServer.HandleRequestSafelyAsync`** | Catches `OperationCanceledException`, `HttpListenerException`, and `ObjectDisposedException` separately, preventing client disconnects from surfacing as unhandled errors. |
| **`ArgumentNullException.ThrowIfNull`** | Used consistently for public-method parameter validation (e.g., `AppSettings.ApplyRuntimeSettings`, `AppDatabase.Insert`). |
| **`ObjectDisposedException.ThrowIf`** | `ProxyServer.Start` correctly guards against use-after-dispose. |
| **Global mutex for single-instance** | `Program.cs` uses a `Global\` mutex, correctly preventing multiple instances across sessions. |
| **`GC.KeepAlive(mutex)`** | Prevents the GC from collecting the mutex before the process exits. |
| **Redaction-by-default** | Request/response body capture defaults to `false` in Release builds, and per-mapping redaction flags default to `true`. Privacy-conscious design. |
| **`ConfigureAwait(false)`** | Used consistently in library/infrastructure code (`ProxyServer`, `PumpPreResponseHeartbeatsAsync`). |
| **Structured logging** | Serilog with CLEF format, rolling file size limits, and configurable retention. |
| **Context overflow auto-summarization** | The retry loop in `HandleChatAsync` with configurable `MaxSummarizationRetries` prevents infinite loops while gracefully handling context window overflows. |

---

## Summary

| Severity | Count |
|---|---|
| Critical | 5 |
| Moderate | 10 |
| Minor / Smell | 8 |

The most impactful fixes are **#1** (double `AppDatabase`), **#2** (missing `Dispose` calls), and **#3** (unguarded `async void OnExit`). These can cause data corruption, resource leaks, and process crashes respectively. The remaining items are hardening and performance improvements that reduce risk under sustained load.
