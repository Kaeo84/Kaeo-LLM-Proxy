# reasoning_effort Is Sent Upstream — Now Visible in Logs (Before / After / Response)

## TL;DR

`reasoning_effort` **was already being injected correctly** into upstream requests. The reason it
looked like it "wasn't being added" is that the request log only captured the **client's original
body** — which never contains `reasoning_effort` (Ollama/OpenAI clients rarely send it, and the proxy
adds it itself). There was no second capture of the body the proxy actually forwarded, so the injected
value was invisible.

This change captures the transformed request as a **separate field** (`UpstreamRequestBody`) and gives
it its own **"Upstream Request Body" tab** in the Log Details window, so you can now troubleshoot
**before (client) / after (upstream) / response** together.

## Root Cause

The injection logic in `OllamaProxyHandler` is correct and verified by tests (see bottom). The gap was
purely observability:

| Path | What was logged before | Problem |
|------|------------------------|---------|
| `/v1/*` passthrough (`PassthroughAsync`) | `bodyText` (raw client body, pre-rewrite) | Injected `reasoning_effort` never shown |
| `/api/chat` translation (`HandleChatAsync`) | raw Ollama body | Ollama clients can't send `reasoning_effort`, so it never appears |
| `/api/generate` (`HandleGenerateAsync`) | raw Ollama body | same |
| `/api/embeddings` (`HandleEmbeddingsAsync`) | raw Ollama body | same |

## Priority Semantics (unchanged, now test-verified)

- **Proxy** — the proxy's configured `ReasoningEffort` always wins: overrides a client value, or is
  injected when the client sent none. **This is the mode you tested.**
- **Provider** — the field is dropped entirely so the hosted provider keeps its platform default.
- **Client App** — whatever the client sent is passed through unchanged; omitted when the client sent none.

## The Transformed Request (what you wanted to see)

With a mapping configured as **Priority = Proxy**, **Reasoning Effort = `high`**, here is the before and
the after for a `/v1/chat/completions` request. The highlighted line is the value the proxy injects.

### Before — client request body (what arrived at the proxy)

```json
{
  "model": "my-model",
  "messages": [
	{ "role": "system", "content": "You are a helpful assistant." },
	{ "role": "user", "content": "Hello" }
  ],
  "stream": true,
  "temperature": 1,
  "top_p": 1
}
```

### After — upstream request body (what the proxy actually sent)

The `+` line is **`reasoning_effort`**, injected by the proxy per the model's **Proxy** priority:

```diff
  {
	"model": "upstream-model-name",
	"messages": [
	  { "role": "system", "content": "You are a helpful assistant." },
	  { "role": "user", "content": "Hello" }
	],
	"stream": true,
	"temperature": 1,
	"top_p": 1,
+   "reasoning_effort": "high"
  }
```

> Note: the proxy emits compact (non-indented) JSON on the wire; it is pretty-printed here for
> readability. The model name is also rewritten through the mapping table (`my-model` → upstream name).

## What Changed in Code

- `RequestLog` gains `UpstreamRequestBody` (the body sent upstream). `RequestBody` keeps its original
  meaning: the client's raw body. → **before / after side-by-side.**
- `OllamaProxyHandler` captures both bodies on all four paths (chat, generate, embeddings, passthrough).
- `NormalizeRequestBody` / `GetInstructionTextForModel` made `internal static` so they are unit-testable.
- `AppDatabase`: new `upstream_request_body` column (baseline schema + `AddColumnIfMissing` migration for
  `requests` / `mcp_requests`), plus INSERT, parameters, `QueryRecent`, `LoadFullLogEntry`, and the reader.
- `MainForm` Log Details: **Request Body** moved to its own tab and a new **Upstream Request Body** tab
  added, alongside the existing **Response Body** tab.

## Test Results (all passing)

```text
Ran 5 test(s). 5 Passed, 0 Failed

  Passed  ProxyPriorityInjectsReasoningEffortWhenClientSendsNone
  Passed  ProxyPriorityOverridesClientReasoningEffort
  Passed  ProviderPriorityDropsClientReasoningEffort
  Passed  ClientAppPriorityPassesClientReasoningEffortThrough
  Passed  ClientAppPriorityOmitsReasoningEffortWhenClientSendsNone
```

These run directly against `OllamaProxyHandler.NormalizeRequestBody`, so they prove the proxy
**does** add `reasoning_effort` when Priority = Proxy — confirming the earlier "missing" behavior was a
logging/visibility gap, not an injection bug.

## How to Confirm in the App

1. Set a model mapping to **Reasoning Effort Priority = Proxy** and pick a value (e.g. `high`).
2. Enable **Collect request details** (on by default in Debug builds).
3. Make a request, then double-click the entry in the log grid.
4. Open the **Upstream Request Body** tab — you will see `"reasoning_effort": "high"` present.
   Compare against the **Request Body** tab (the client's original) and **Response Body**.
