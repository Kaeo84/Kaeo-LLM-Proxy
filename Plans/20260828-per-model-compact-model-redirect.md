# Per-Model Context-Summarize (/compact) Redirect

## Understanding
GitHub Copilot's `/compact` sends an OpenAI-compatible `/v1/chat/completions` request whose first
(system) message is a distinctive "produce an authoritative, self-contained summary" prompt. The
proxy detects these cheaply (head-of-first-message check) and routes them to a smaller/faster model
chosen per-model in settings, via a dropdown. Complements the existing `EnableAutoSummarization`
feature (which auto-retries on overflow) — this one redirects the client's own /compact requests.

## Assumptions
- `/compact` arrives through the OpenAI passthrough path (`/v1/*` -> `PassthroughAsync` ->
  `NormalizeRequestBody`); the Ollama `/api/chat` and `/api/generate` paths are handled too.
- Detection inspects only the head (~512 chars) of the first message content for distinctive
  markers (`authoritative, self-contained summary`, `<ConversationSummary>`, `ReasoningScratchpad`).
- "Redirect to model X" means treat the request as a normal request to the compact model: its
  upstream URL, credential, sampling, and instruction-set settings all apply.
- The compact model is chosen by proxy name from a dropdown listing all configured proxy models.
- `ModelMapping` is persisted column-by-column in SQLite `model_mappings`, so a new column +
  migration is required.

## Steps
- [x] 1. Add `ContextSummarizeModelName` to `ModelMapping` + `Clone()` in Core/Models/AppSettings.cs
- [x] 2. Add `DebugNotes.ContextSummarizeRedirect` audit helper in Services/DebugNotes.cs
- [x] 3. Add `IsContextSummarizeRequest`, `ResolveEffectiveModel`, `GetFirstMessageContent` to Services/OllamaProxyHandler.cs
- [x] 4. Wire `NormalizeRequestBody` (OpenAI path) to route via the effective model
- [x] 5. Wire `HandleChatAsync` (Ollama chat path) to route via the effective model + debug note
- [x] 6. Wire `HandleGenerateAsync` (Ollama generate path) to route via the effective model
- [x] 7. Persist `context_summarize_model_name` in Infrastructure/AppDatabase.cs (schema, migration, SELECT, INSERT, params, read)
- [x] 8. Add the per-model compact-model dropdown to ModelMappingDialog.cs (field, property, populate, layout row 6, control setup, load/save)
- [x] 9. Add unit tests in Kaeo LLM Proxy.Tests/ContextSummarizeRedirectTests.cs (14 tests)
- [x] 10. Build the solution (0 errors) and run all tests (82 pass)
