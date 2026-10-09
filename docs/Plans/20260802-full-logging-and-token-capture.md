# Full request/response logging for all endpoints + complete token usage capture

## Understanding

Store full request AND response bodies for all API calls passing through the proxy
(previously only chat/completions paths stored responses), and capture the complete
upstream `usage` block — including `prompt_tokens_details.cached_tokens`,
`completion_tokens_details.reasoning_tokens`, and `total_tokens` — on every request,
including the transparent `/v1/*` passthrough which previously logged zero tokens.

## Approach

- New `RequestLog` fields: `TotalTokens`, `CachedPromptTokens`, `ReasoningTokens`
  (0 = not reported), persisted via three new `requests` columns appended at the end of
  the schema (baseline DDL + programmatic `MigrateRequestsTable` ALTERs, matching the
  existing migration pattern).
- `LlamaCppUsage` extended with `prompt_tokens_details` / `completion_tokens_details`;
  `FillTokenStats` copies all values.
- Passthrough usage capture without full-body buffering: lightweight `SseUsageSniffer`
  (line-framing buffer; only JSON-parses `data:` lines containing `"usage"`) fed from
  both SSE copy methods via a new optional `Action<LlamaCppUsage>? onUsage` parameter.
  Non-streaming completions use a new `Action<string>? onBody` callback on
  `CopyNonStreamingChatResponseAsync` to parse usage and capture the (redacted) body.
- Response bodies for locally synthesized endpoints (`/api/tags`, `/api/ps`, `/api/show`)
  stored raw when `CollectResponseDetails`; `/api/embeddings` stored through
  `RedactResponseBodyForLog` (marker by default, avoiding MB-scale vector storage);
  upstream error bodies stored raw into `log.ResponseBody` when still null.
- `log.Streaming` now set for passthrough requests.

## Steps

- [x] 1. Extend `Core/Models/RequestLog.cs` — add `TotalTokens`, `CachedPromptTokens`, `ReasoningTokens`.
- [x] 2. Extend `Core/Models/OllamaTypes.cs` — usage detail classes + properties.
- [x] 3. Update `FillTokenStats` to copy total/cached/reasoning values.
- [x] 4. Add `SseUsageSniffer` sealed class near `ThinkTagExtractor`.
- [x] 5. Wire `onUsage` into `CopyStreamWithSseHeartbeatsAsync` and `CopyOpenAiChatCompletionSseStreamAsync`.
- [x] 6. Rework `CopyNonStreamingChatResponseAsync` with optional `onBody` callback.
- [x] 7. Update `PassthroughAsync` — streaming flag, usage callbacks, non-SSE body capture, error-body storage.
- [x] 8. Ollama handlers — tags/ps/show/embeddings response bodies + error bodies on chat/generate/embeddings.
- [x] 9. `Infrastructure/AppDatabase.cs` — schema, migration, INSERT, SELECTs, readers.
- [x] 10. `Core/Services/StatisticsService.cs` — copy new fields in `CreateSummary`.
- [x] 11. `MainForm.cs` — show total/cached/reasoning in log details.
- [x] 12. Build — compiled clean (output .exe locked by running app; close app and rebuild).
- [x] 13. Save plan to `Plans/` and mark steps completed.
- [x] 14. Git commit with summary message.
