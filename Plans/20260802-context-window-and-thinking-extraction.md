# Add Context Window Reporting and Wire Up Thinking-Tag Extraction

## Understanding
GitHub Copilot's context meter shows 100%+ because the proxy doesn't advertise model context windows in discovery responses. Additionally, the thinking-tag extraction feature (moving `<think>` blocks into `reasoning_content`) is fully implemented in the handler but unreachable: `ThinkingMode` has no DB persistence or GUI control, so it's always `Off`, causing thinking text to muddy the chat window.

## Root Causes Confirmed
1. **Context Window**: `ModelMapping` has no `ContextWindowTokens` field; `CreateOllamaModelInfo` (OllamaProxyHandler.cs:~1861) doesn't emit `*.context_length` keys; clients fall back to wrong defaults.
2. **Thinking Extraction**: `ThinkingMode` enum exists on `ModelMapping`, handler logic is complete (OpenAiSseRewriter at line 1154, CopyNonStreamingChatResponseAsync at 826, gated correctly on `/v1/chat/completions` path per lines 729-736), but no DB column/GUI/round-trip → always `Off`.

## Approach
- Add `ContextWindowTokens` (int, default 0 = auto) and ensure `ThinkingMode` persistence.
- Use a global default `DefaultContextWindowTokens = 131072` (conservative fallback; user overrides per model).
- No upstream fetch (OpenAI `/v1/models` lacks `context_length`; QwenCloud doesn't add it).
- DB: append both fields at end of SELECT/INSERT/CREATE (ordinals 19, 20) to avoid renumbering existing ordinals.
- GUI: add TextBox for context window (empty/0 = auto, show default as hint) + CheckBox for thinking extraction.
- Handler: emit `{family}.context_length` and `general.context_length` in `CreateOllamaModelInfo` using `mapping.GetEffectiveContextWindow()`.

## Key Files
- `Core/Models/AppSettings.cs` — add fields, Clone(), resolver, const
- `Infrastructure/AppDatabase.cs` — schema migration, SELECT/INSERT/Read ordinals 19-20
- `Core/Services/OllamaProxyHandler.cs` — CreateOllamaModelInfo emission (~line 1861)
- `ModelMappingDialog.cs` — UI controls, properties, row layout (RowCount 22)
- `MainForm.cs` — row.Tag construction, BtnSaveSettings mapping

## Assumptions
- Copilot uses `/v1/chat/completions` (OpenAI path) where extraction is already wired (verified lines 729-736).
- Ollama `/api/chat` path doesn't extract (known limitation; out of scope).
- Global default 131072 is safe (too-large is better than too-small; auto-summarization handles overflow).

## Current DB Schema (for reference)
SELECT ordinals 0-18: is_enabled, proxy_name, model_name, enable_thinking_compatibility, supports_vision, enable_heartbeats, upstream_type, upstream_url, upstream_timeout_seconds, repeat_penalty, temperature, enable_auto_summarization, preserve_recent_message_count, max_summarization_retries, instruction_set_name, redact_request_bodies, redact_response_bodies, redact_sensitive_json_fields, credential_name.

Append: thinking_mode(19), context_window_tokens(20).

## Risks & Open Questions
- Context window values are NOT auto-fetched (API doesn't provide); rely on user override + global default.
- `/v1/models` synthesis from mappings: conditional step (check if handler exists; if passthrough-only, skip).

## Steps
1. [x] Create this plan file `Plans/20260802-context-window-and-thinking-extraction.md`
2. [x] Model: add fields to `ModelMapping` in `Core/Models/AppSettings.cs`
3. [x] DB schema: extend `AppDatabase.cs` (CREATE TABLE, SELECT, INSERT, params, Read, migration)
4. [x] Handler emission: add context_length to `CreateOllamaModelInfo` in `OllamaProxyHandler.cs`
5. [x] Dialog UI: add controls to `ModelMappingDialog.cs` (context window TextBox + thinking CheckBox)
6. [x] Dialog wiring: read/write in `ShowConfigureDialog`
7. [x] MainForm round-trip: add fields to both mapping initializers
8. [x] Build verification
9. [ ] Git commit

**Update this file with [x] after completing each step.**

---

## Final Dialog Row Layout (RowCount = 22; Percent filler on row 19)
0. Proxy Name  
1. Upstream URL  
2. Upstream Type  
3. Credential  
4. Model Name + Fetch  
5. Instruction Set  
6. Upstream Timeout  
7. **Context Window** [NEW]  
8. Temperature  
9. Repeat Penalty  
10. chkIsEnabled  
11. chkEnableThinkingCompatibility  
12. **chkExtractThinkTags** [NEW]  
13. chkSupportsVision  
14. chkEnableHeartbeats  
15. chkEnableAutoSummarization  
16. Preserve Recent Exchanges  
17. Max Summarization Retries  
18. chkRedactRequestBodies  
19. chkRedactResponseBodies (Percent filler)  
20. chkRedactSensitiveJsonFields  
21. flpButtons  
