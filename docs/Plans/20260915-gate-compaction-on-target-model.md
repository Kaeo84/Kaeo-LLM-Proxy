# Gate Compaction on a Selected Target Model

## Understanding
The per-mapping "Compaction Model" dropdown (`ModelMapping.ContextSummarizeModelId`) should be the single target used by BOTH automatic and manual compaction. Today both paths silently fall back to the original model when no target is selected, so compaction "still happens" even with the dropdown on (None). When the dropdown is (None), neither auto nor manual compaction does anything (manual passes the body through untouched). A new "Redirect Manual Compaction" checkbox gates the manual path. Clear help text so (None) = no compaction is possible.

## Assumptions
- The global `AppSettings.CompactModelProxyName` override stays in place (app-wide); the per-mapping dropdown is the primary target. A resolved target = per-mapping dropdown OR the global override.
- "Pass through untouched" for manual = return the original request body unchanged with HTTP 200 (these endpoints never forward to upstream).
- New `RedirectManualCompaction` defaults to `false` (unchecked) for existing rows.
- Auto compaction still requires its existing gates (AutoCompactPaths + threshold) AND a resolved target; if no target, it does nothing (no fallback to original model).

## Approach
1. Model: add `bool RedirectManualCompaction` to `ModelMapping` and copy it in `Clone()`.
2. Database: add `redirect_manual_compaction` column (schema + migration + SELECT + INSERT + parameter + reader).
3. Dialog: add `_chkRedirectManualCompaction` checkbox, a dynamic `_lblCompactionStatus` help label, update the model dropdown tooltip, wire load/save, and lay out both in the compaction group.
4. Handler manual path: resolve target; if `!RedirectManualCompaction` OR no target → write original body back (200) and return; else run compaction with the resolved target.
5. Handler auto path: require a resolved target; if none, return (false, null) instead of falling back to the original model.
6. Tests: cover the new gating (manual pass-through when unchecked/None, manual redirect when checked+target, auto no-op when no target) and the new column round-trip.

## Key Files
- Kaeo LLM Proxy Core/Models/AppSettings.cs — ModelMapping property + Clone
- Kaeo LLM Proxy Infrastructure/AppDatabase.cs — schema/migration/SELECT/INSERT/param/reader
- ModelMappingDialog.cs — checkbox, help label, tooltip, load/save, layout
- Kaeo LLM Proxy Services/OllamaProxyHandler.cs — manual + auto gating
- Kaeo LLM Proxy.Tests/ContextCompactionTests.cs — new behavior tests

## Steps
- [x] Add `RedirectManualCompaction` to ModelMapping and copy it in Clone()
- [x] Add `redirect_manual_compaction` column to schema + migration in AppDatabase
- [x] Wire the new column into SELECT, INSERT, parameters, and reader in AppDatabase
- [x] Add `_chkRedirectManualCompaction` field and property to ModelMappingDialog
- [x] Add `_lblCompactionStatus` help label + `UpdateCompactionStatus()` and hook the model combo's SelectedIndexChanged
- [x] Update the model dropdown tooltip to state it is the target for both auto and manual compaction
- [x] Lay out the checkbox and status label in the compaction group
- [x] Wire load (ShowConfigureDialog) and save for RedirectManualCompaction
- [x] Gate the manual compaction handlers on RedirectManualCompaction + resolved target (pass through otherwise)
- [x] Gate the auto compaction path on a resolved target (no fallback to original model)
- [x] Update/add tests for the new gating and column round-trip
- [x] Build the solution and run the compaction test suites
- [x] Commit the change

## Notes
- Commit: e6b87ac "Gate compaction on a selected target model"
- Test result: 65 passed; 2 pre-existing failures (CompactAsync_FlattensToolCallsIntoToolcallsBlock, CompactAsync_ProxyFormat_KeepsTrailingToolPairAfterSummary) also fail on baseline — unrelated to this change.
