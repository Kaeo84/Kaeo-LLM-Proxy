# Remove Global CompactModelProxyName

## Understanding
Completely remove the global `AppSettings.CompactModelProxyName` setting and every place it is read or tested. The per-mapping `ContextSummarizeModelId` dropdown becomes the only compaction target. No global override remains.

## Assumptions
- `CompactModelProxyName` is never persisted in settings.jsonc (only referenced in code/tests), so no config-file cleanup is needed.
- After removal, the 4 handler resolution sites resolve the target solely from the per-mapping `ContextSummarizeModelId`.
- Tests that exist solely to exercise the global setting are removed; tests that incidentally set it are edited.

## Steps
- [x] Remove the `CompactModelProxyName` property from AppSettings.cs
- [x] Simplify the proactive auto-compaction target resolution in OllamaProxyHandler.cs
- [x] Simplify the reactive auto-compaction target resolution in OllamaProxyHandler.cs
- [x] Simplify the HandleCompactAsync manual target resolution in OllamaProxyHandler.cs
- [x] Simplify the HandleManualCompactAsync manual target resolution in OllamaProxyHandler.cs
- [x] Remove the global-setting and global-routing tests, and clean up the configuration tests in ContextCompactionTests.cs
- [x] Build the solution and run the compaction test suites
- [x] Commit the change

## Notes
- Commit: 18d9167 "Remove global CompactModelProxyName setting" (3 files changed)
- Test result: 60 passed; 2 pre-existing failures (CompactAsync_FlattensToolCallsIntoToolcallsBlock, CompactAsync_ProxyFormat_KeepsTrailingToolPairAfterSummary) also fail on baseline — unrelated to this change.
- Replaced the 3 global-routing tests with 2 per-mapping routing tests (ResolvesPerMappingTarget, NoTargetWhenNoneConfigured).
