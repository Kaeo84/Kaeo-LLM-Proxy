# 20260914 - Proxy Phase B: Microsoft.Extensions.AI internal representation

Design document for plan step 11 (deferred Phase B). Phase A (the VS extension adopting
MEAI: `MeaiAdapter`, `OllamaChatClient : IChatClient`, `McpToolAIFunction`, typed
reasoning/tool content) is complete and builds green. This document designs the same
migration for the proxy itself, per the standing goal: "implement the other upstream and
downstream options in the Proxy" and endpoint emulation that stays compliant with the
vendor specs vendored under `API Specs\`.

## Understanding
Today `OllamaProxyHandler` (~6,100 lines) hand-writes every wire translation as bespoke
JSON-node code: Ollama /api/chat to and from llama.cpp OpenAI chat completions,
/api/generate to /v1/completions, batch embeddings, plus an OpenAI passthrough path with
an SSE rewriter. Provider-specific quirks are patched in place: thinking is handled by
the per-mapping `ThinkingMode` string-hacks (LeaveInline / MoveToReasoningContent /
StripFromOutput + a streaming think-tag extractor), tool-call ids by
`MapMessagesWithToolCorrelation`, inline XML tool-call synthesis by the SSE rewriter,
system-message shape and declared-tool filtering by recent targeted fixes (the sanitizer
and system-merge). Every new client surface or new upstream multiplies these mappings
pairwise - the real reason thinking "has been a huge challenge".

Phase B replaces the pairwise mappings with a hub-and-spoke design: one internal
representation (Microsoft.Extensions.AI `ChatMessage` / `ChatResponseUpdate` with typed
content parts - `TextContent`, `TextReasoningContent`, `FunctionCallContent`,
`FunctionResultContent`, `UsageContent`), provider clients on the spoke ends upstream,
and thin protocol serializers per client surface downstream. This is the same shape the
Vercel AI SDK uses for its unified content-part streams (see `vercel-ai-sdk reference\`),
and exactly what the extension now does after Phase A.

## Assumptions
- Services (net10) can reference `Microsoft.Extensions.AI` / `.Abstractions` directly
  (verified available; no VSIX payload concerns on this side).
- Byte-fidelity of the existing Ollama + OpenAI surfaces is a hard constraint: all 177
  current tests (incl. the 13 sanitizer parity tests) must stay green at every increment;
  golden-stream fixtures are captured from the old code first.
- Pure OpenAI passthrough with no rewriting stays a byte-copy fast path; the IR is used
  only when the response actually needs translation or rewriting (perf + fidelity).
- `ThinkingMode` and `ReasoningEffortFormat` survive as *serializer options* describing
  how reasoning parts are rendered onto a given wire - they stop being pipeline behavior.
- Declared-tool filtering and system-message merging become structural: declared tools
  already live in `ChatOptions.Tools`, so anything not matching is simply not projected;
  history is always `ChatMessage`s, so the single-leading-system rule becomes a property
  of the outbound serializer.
- A shared netstandard2.0/net48+net10 multi-target project for the adapter code is
  acceptable to finish the abandoned `Kaeo LLM Proxy VS Extension.Core` attempt (only
  obj/ leftovers exist today); the test project keeps linking it or referencing it.
- Anthropic/Gemini/Mistral/Cohere downstream emulation is out of scope until an upstream
  demand exists; the serializer seams below are what make it cheap later.

## Approach
1. **Extract a pure IR core** (`Kaeo LLM Proxy Services/Translation/`):
   `InboundParser` (Ollama /api/chat + /api/generate + OpenAI /v1 bodies -> `ChatMessage`
   list, preserving tool-call correlation) and `OutboundWriter` (IR -> `LlamaCppChatRequest`
   JSON, IR -> Ollama NDJSON chunk sequence, IR -> OpenAI chat/SSE frames). Port the
   proven mapping logic from the extension `MeaiAdapter` plus the proxy's own quirks
   (think-tag extraction becomes `TextReasoningContent` ingestion; XML tool-call
   synthesis becomes a part producer). 100% unit-testable - no sockets, no streams.
2. **Retrofit /api/chat first** (Ollama client -> llama.cpp/OpenAI upstream): request
   parsing and response streaming route through the IR behind a per-request flag, with
   golden NDJSON parity tests generated from the legacy path before cutover. Heartbeats,
   usage sniffing and token stats hook IR `UsageContent` instead of raw JSON sniffing.
3. **IR the passthrough rewriter**: when a request needs no rewriting (no thinking-mode,
   no XML synthesis, no system-merge, no sanitizer hits, model name unchanged) keep the
   existing byte path untouched; otherwise parse-to-IR, transform typed parts, re-emit.
   The sanitizer/system-merge fixes collapse into: drop/merge content parts by role,
   project only `ChatOptions.Tools` names.
4. **Provider clients**: upstream selection becomes a per-mapping `UpstreamKind`
   (llama.cpp/OpenAI-compatible via `Microsoft.Extensions.AI.OpenAI`; Ollama native via
   `Microsoft.Extensions.AI.Ollama`; direct OpenAI/Anthropic later) each consumed as an
   `IChatClient` - new upstream = one client registration + config UI, not a new mapping
   matrix. Streaming middleware (heartbeats, logging, compaction-413 probe) wraps the
   `IChatClient` like the extension already does.
5. **Shared adapter project**: create `Kaeo LLM Proxy VS Extension.Core` properly
   (multi-target net48;netstandard2.0), move `MeaiAdapter` there, have Services' IR core
   and the extension both consume it; deletes the Compile-Link duplication.
6. **Spec-driven conformance harness**: fixture loader over `API Specs\`
   (openai-openapi/openapi.yaml, google-gemini\*, mistral\*, cohere-openapi.yaml) that
   schema-validates proxied request/response pairs and SSE event shapes per surface
   (one event per data-frame is a known rewriter constraint); wire it into the test
   project as theory-driven contract tests per (client surface, upstream) pair.

## Steps

- [x] B1. Add MEAI package refs to Services; create Translation/ pure-IR parsers+writers with golden-stream parity fixtures captured from the legacy handler
- [x] B2. /api/chat request translation routed through the IR behind the UseIrTranslation flag (golden structural-parity tests vs the legacy mapper: 7/7; the flag stays default-off until it holds under live use; response-relay IR-ification folds into B3's rewriter work)
- [ ] B3. IR-ize the passthrough rewriter (byte path stays for no-rewrite requests; sanitizer + system-merge collapse into part-level rules)
- [ ] B4. Per-mapping UpstreamKind consumed as IChatClient (MEAI OpenAI/Ollama clients); config UI + provider registration replaces pairwise mapping
- [x] B5. Revive Kaeo LLM Proxy VS Extension.Core as the shared multi-target adapter project; delete the Compile-Link duplication
- [ ] B6. Spec-driven conformance harness over API Specs\ (schema validation + SSE event-shape contract tests per client/upstream pair)

## Key Files
- Kaeo LLM Proxy Services/OllamaProxyHandler.cs - legacy translation monolith to decompose
- Kaeo LLM Proxy Services/Translation/* (new) - IR inbound parsers / outbound writers / provider clients
- Kaeo LLM Proxy VS Extension.Core/* (new, revived) - shared MeaiAdapter consumed by proxy + extension
- Kaeo LLM Proxy Core/Models/AppSettings.cs - per-mapping UpstreamKind; ThinkingMode re-documented as serializer option
- Kaeo VS Extension/Core/MeaiAdapter.cs - proven IR mapping to promote into the shared project
- API Specs/* - authoritative schemas driving the conformance fixtures
- Plans/20260827-openai-passthrough-parity.md, 20260803-move-thinking-reasoning-content-format.md,
  20260806-per-model-reasoning-effort.md - prior ad-hoc work this design subsumes
