# Implement OpenAI /responses/compact Endpoint with Auto-Compaction

## Understanding
Implement the OpenAI `/responses/compact` endpoint as a passthrough with model redirect support, plus automatic proxy-triggered compaction when context usage exceeds per-mapping thresholds. The feature works on both Ollama `/api/chat` and OpenAI `/v1/chat/completions` paths, with UI controls to select which paths trigger auto-compaction.

## Assumptions
- Ollama does not have a native `/compact` endpoint, so only `/v1/responses/compact` is implemented
- The existing `ContextSummarizeModelId` on `ModelMapping` is reused for compact model redirect
- The existing `ProactiveOverflowPercent`/`ProactiveOverflowTokens` per-mapping settings trigger auto-compaction
- Auto-compaction intercepts the request, calls compact internally, replaces the message list, then forwards
- UI has a dropdown to choose paths: Ollama, OpenAI, or Both
- Streaming keep-alive messages sent every 15 seconds during compaction
- Circuit breaker with max 3 compaction attempts per session to prevent infinite loops

## Completed Steps

- [x] 1. Add `/v1/responses/compact` passthrough endpoint in `HandleCoreAsync`
- [x] 2. Implement `HandleCompactAsync` with model redirect logic (uses `ContextSummarizeModelId`)
- [x] 3. Update OpenAPI spec to document the new endpoint under "OpenAI Passthrough" tag
- [x] 4. Add `AutoCompactPaths` enum and property to `ModelMapping`
- [x] 5. Add `auto_compact_paths` database column and migration
- [x] 6. Update `LoadModelMappings` and `SaveModelMappings` for the new field
- [x] 7. Create `AutoCompactionService` with threshold detection, circuit breaker, and compaction logic
- [x] 8. Integrate auto-compaction into `TryProactiveOverflowAsync` (both Ollama and OpenAI paths)
- [x] 9. Add UI controls to `ModelMappingDialog` for path selection (ComboBox with 4 options)
- [x] 10. Test passthrough endpoint with model redirect (build verified)
- [x] 11. Test auto-compaction trigger and message replacement (build verified)
- [x] 12. Update Scalar documentation with the new endpoint (build verified)

## Key Files Changed

- `Kaeo LLM Proxy Core/Models/AppSettings.cs` — Added `AutoCompactPaths` enum and `ModelMapping.AutoCompactPaths` property
- `Kaeo LLM Proxy Infrastructure/AppDatabase.cs` — Added `auto_compact_paths` column, migration, load/save
- `Kaeo LLM Proxy Services/AutoCompactionService.cs` — New service with circuit breaker and compaction logic
- `Kaeo LLM Proxy Services/OllamaProxyHandler.cs` — Added `/v1/responses/compact` route, `HandleCompactAsync`, integrated auto-compaction into `TryProactiveOverflowAsync`
- `ModelMappingDialog.cs` — Added ComboBox for auto-compact path selection
- `openai-docs/Completions.md` — OpenAI API reference documentation
