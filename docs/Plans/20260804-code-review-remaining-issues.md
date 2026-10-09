# Resolve Remaining CODE_REVIEW.md Issues (#15–#23)

**Date:** 2026-08-04
**Status:** Complete

## Understanding
CODE_REVIEW.md lists 23 findings; #1–#14 are already resolved. This task resolves the remaining items #15–#23 (one moderate redaction concern plus minor issues/code smells) in the Kaeo LLM Proxy codebase, updates the review document to reflect resolution, verifies the build, and commits.

## Assumptions
- Issue #17 (`BuildHttpClient` unused parameter) is already fixed in code (`BuildHttpClient()` takes no parameter); only the review doc needs updating.
- Implicit usings are enabled and the WinForms SDK provides global usings for `System.Drawing`/`System.Windows.Forms`, so those explicit directives in `ModelMappingDialog.cs` are redundant. `System.Text` and `System.Text.Json` are NOT implicit and must stay.
- No test project exists in the solution; verification is via build.
- `FetchUpstreamModelsAsync` in `ModelMappingDialog.cs` is a sibling of issue #20 (new HttpClient per call) and gets the same treatment with its own shared client (different timeout requirement).

## Approach
Fix each finding with a minimal, targeted edit:
1. **#15** (`Core/Services/OllamaProxyHandler.cs`): remove content-bearing fields (`prompt`, `system`, `messages`, `input`, `content`) from `IsSensitiveJsonProperty` so logs remain useful when users opt into body capture; keep keys/tokens/secrets/passwords. Update the doc comment on `ModelMapping.RedactSensitiveJsonFields` in `Core/Models/AppSettings.cs`.
2. **#16** (`Core/Services/OllamaProxyHandler.cs`): fix over-indentation of the `ResolveUpstream` line in `HandleChatAsync` (~line 2275).
3. **#18** (`ModelMappingDialog.cs`): drop usings covered by implicit/global usings; keep `System.Text`, `System.Text.Json`, and project namespaces.
4. **#19** (`Core/Services/PerformanceService.cs`): replace empty catch-all in `Sample` with `catch (Exception ex)` + `Log.Debug`.
5. **#20** (`MainForm.cs` + sibling in `ModelMappingDialog.cs`): replace per-request `new HttpClient` with shared static clients (test console: infinite timeout, per-request CTS; model fetch: 10 s timeout).
6. **#21** (`Infrastructure/AppDatabase.cs`): move WAL setup out of the schema batch into an idempotent `EnsureWalJournalMode` helper that only sets WAL when the current journal mode differs; tolerate sharing failures gracefully per repo rules.
7. **#22** (`Core/Services/StatisticsService.cs`): document the intentional soft-cap semantics of the trim loop in `AddLog` (no lock on the hot path by design).
8. **#23** (`Infrastructure/ProxyServer.cs`): remove redundant `listener?.Stop()` in `StopAsync`; `Close()` already stops the listener (matches `Dispose`).
9. Update CODE_REVIEW.md marking #15–#23 `[RESOLVED]` (#17 noted as already fixed in code).
10. Build, then git commit per repo guidelines.

## Key Files
- Core/Services/OllamaProxyHandler.cs — redaction field list (#15), indentation (#16)
- Core/Models/AppSettings.cs — redaction doc comment (#15)
- ModelMappingDialog.cs — using cleanup (#18), shared fetch client (#20 sibling)
- Core/Services/PerformanceService.cs — empty catch (#19)
- MainForm.cs — shared test-console client (#20)
- Infrastructure/AppDatabase.cs — WAL pragma (#21)
- Core/Services/StatisticsService.cs — soft-cap comment (#22)
- Infrastructure/ProxyServer.cs — redundant Stop (#23)
- CODE_REVIEW.md — status updates

## Risks & Open Questions
- Removing content fields from redaction changes what appears in logs when sensitive-field redaction is on — this is the intended behavior per the review recommendation.
- WAL switch requires brief exclusive access; wrap in try/catch (SqliteException/IOException) with a warning so multi-instance sharing violations degrade gracefully.

## Steps
- [x] 1. Save plan copy to Plans/ folder
- [x] 2. Fix #15: limit `IsSensitiveJsonProperty` to truly sensitive fields and update `AppSettings` doc comment
- [x] 3. Fix #16: correct indentation in `HandleChatAsync`
- [x] 4. Fix #18: remove redundant using directives in ModelMappingDialog.cs
- [x] 5. Fix #19: add Debug logging to PerformanceService.Sample catch
- [x] 6. Fix #20: shared static HttpClient for MainForm test console and ModelMappingDialog model fetch
- [x] 7. Fix #21: conditional WAL mode in AppDatabase.InitializeDatabase
- [x] 8. Fix #22: document soft-cap trim semantics in StatisticsService.AddLog
- [x] 9. Fix #23: remove redundant `Stop()` in ProxyServer.StopAsync
- [x] 10. Update CODE_REVIEW.md statuses for #15–#23
- [x] 11. Build workspace and verify no errors
- [x] 12. Git commit the changes
