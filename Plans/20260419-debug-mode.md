# Add Debug Mode: Before/After Translation Logging with Override Audit Trail

## Context

Goal: a new `DebugMode` setting. When enabled, every proxied request's log entry makes the
proxy's transformations visible and verifiable: the Ollama→OpenAI request before/after, the
raw OpenAI→Ollama response before/after, and a list of every settings-driven override applied
(temperature, repeat penalty, instruction-set injection, reasoning effort, model rewrite),
each marked injected / replaced / passed through / omitted.

## Steps
- [x] Save this plan to `Plans/20260419-debug-mode.md` — mark steps off with `[x]` as they complete.
- [x] Add `DebugMode` to `RuntimeSettings` and `AppSettings` in `Kaeo LLM Proxy Core\Models\AppSettings.cs` — `[JsonIgnore]` on the `AppSettings` copy, wired through `CreateRuntimeSettings` and `ApplyRuntimeSettings`.
- [x] Add `DebugSummary` and `UpstreamResponseBody` (both `string?`) to `RequestLog` in `Kaeo LLM Proxy Core\Models\RequestLog.cs` — documented with the before/after semantics.
- [x] Update `Kaeo LLM Proxy Infrastructure\AppDatabase.cs` — `debug_mode` column on `runtime_settings` (baseline + `MigrateRuntimeSettingsTable`), added to `LoadRuntimeSettings` SELECT/reader and `SaveRuntimeSettings` INSERT/params; `debug_summary` + `upstream_response_body` columns on `requests` and `mcp_requests` (baseline + `MigrateRequestsTable` via `AddColumnIfMissing`); both added to `Insert` and `AddRequestLogParameters`; both added to `LoadFullLogEntry` SELECT and `ReadRequestLog` trailing ordinals 24/25.
- [x] Implement the debug-notes builder — new `internal static class DebugNotes` in `Kaeo LLM Proxy Services\DebugNotes.cs` (model resolution, sampling decision, instruction injection, reasoning-effort decision with wire-format listing); pure and unit-testable.
- [x] Wire the Ollama→OpenAI request path — `HandleChatAsync` records model resolution, assistant-prefill removal, instruction-set injection, temperature/repeat_penalty/reasoning_effort decisions, tool count, and response format into `log.DebugSummary`; captures `RequestBody`/`UpstreamRequestBody` when either `CollectRequestDetails` or `DebugMode` is on.
- [x] Wire the OpenAI→Ollama response path — non-streaming branch and error branch capture raw upstream body into `log.UpstreamResponseBody` when `DebugMode`; streaming `StreamChatToOllamaAsync` gained a `collectRawUpstream` flag accumulating raw `data:` SSE lines into a `PooledCharBuffer`.
- [x] Wire the `/v1` passthrough path — `NormalizeRequestBody` appends decision lines to `log.DebugSummary` when `settings.DebugMode` (via `BuildNormalizeDebugNotes` + `ReadJsonNumber`); the Test Console call site needs no change.
- [x] Update the settings UI — `MainForm.Designer.cs`: `_chkDebugMode` declared/instantiated/configured, inserted at row 7 of `_tlpSettings` (RowCount 16→17, subsequent rows shifted); `MainForm.cs`: `CheckedChanged` wiring, `LoadSettingsToForm` and `SaveGeneralSettings` bindings.
- [x] Update the log details dialog — `ShowLogDetails` renders `DebugSummary` at the top of the Summary tab under a `── Debug: applied overrides & transformations ──` header, and adds an "Upstream Response Body (OpenAI)" tab before the (renamed) "Response Body (Ollama)" tab.
- [x] Add unit tests — `Kaeo LLM Proxy.Tests\DebugNotesTests.cs` (14 tests: sampling replace/inject/omit/passthrough, instruction injection, reasoning multi-format inject/replace/omit/passthrough, model rewrite, and `NormalizeRequestBody` end-to-end DebugSummary population on/off). DB round-trip test skipped (no temp `AppDatabase` test infra exists).
- [x] Build + test + commit — full solution builds; all 57 tests pass.
