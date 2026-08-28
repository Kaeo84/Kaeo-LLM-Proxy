# Enhance OpenAI Passthrough with Full Proxy Feature Parity

## Understanding
The proxy serves two API surfaces: the Ollama-native `/api/*` translation path (full-featured)
and the OpenAI `/v1/*` passthrough path (used by VS Copilot, OpenAI SDKs). The passthrough has
already gained model rewriting, sampling/reasoning priorities, instruction injection,
thinking-mode transforms, SSE XML tool-call extraction (`OpenAiSseRewriter`), heartbeats,
proactive overflow, and usage stats. A gap analysis of `PassthroughAsync` vs
`HandleChatAsync`/`HandleShowAsync` in `OllamaProxyHandler.cs` found four remaining parity
gaps that this plan closes.

## Gaps
1. **Non-streaming XML tool-call extraction** — `/v1/chat/completions` with `stream:false`:
   the Ollama path runs `ExtractXmlToolCalls` on response content and the streaming passthrough
   does it via `OpenAiSseRewriter`, but the non-streaming passthrough forwards raw XML.
2. **DebugMode raw-response capture** — the Ollama path captures the raw upstream body into
   `log.UpstreamResponseBody` when DebugMode is on; passthrough never does (request-side
   bodies also only capture under `CollectRequestDetails`, while the Ollama path captures under
   CollectRequestDetails-or-DebugMode).
3. **`/v1/completions` streaming support** — `isStreamingRequest` only covers chat completions,
   so legacy completions streaming gets no pre-committed SSE headers/heartbeats and wrong
   `log.Streaming`.
4. **`GET /v1/models/{model}`** — currently forwarded upstream (inconsistent across providers);
   `/api/show` is answered locally. Serve the single-model lookup locally from mappings.

## Steps
- [x] 1. Save plan file to `Plans/20260827-openai-passthrough-parity.md`
- [x] 2. Add non-streaming XML tool-call extraction: extend `TransformNonStreamingChatBody`
	  (make `internal static`) with `extractToolCalls`, thread through
	  `CopyNonStreamingChatResponseAsync` (fast path respects it), add
	  `BuildOpenAiToolCallsArray` helper reusing `ExtractXmlToolCalls`
- [x] 3. Restructure `PassthroughAsync` response section into one capture-aware flow:
	  DebugMode `UpstreamResponseBody` capture (SSE chat via rawCapture, SSE other via
	  ResponseCaptureStream, non-streaming via multicast onBody, errors directly);
	  request-side capture under CollectRequestDetails-or-DebugMode; hoist
	  `isCompletionPath` so `/v1/completions` streaming gets SSE pre-commit + heartbeats
- [x] 4. Route `GET /v1/models/{id}` to new local `HandleV1ModelAsync` (OpenAI model object;
	  404 OpenAI-style error when unmapped) + OpenAPI spec entry
- [x] 5. Add `PassthroughToolCallExtractionTests` for the non-streaming extraction
- [x] 6. Build solution, run all tests (63 passed)
- [x] 7. Commit to git
