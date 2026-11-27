# Fix Move-to-reasoning_content streaming delta formatting

## Context & Findings
The user asked to verify that when a model mapping's Thinking Handling is set to Move thinking into reasoning_content (ThinkingMode.MoveToReasoningContent), streamed chunks are formatted like:

```json
{"choices":[{"finish_reason":null,"index":0,"delta":{"reasoning_content":"\n\n"}}],"created":1786657431,"id":"chatcmpl-...","model":"Bonsai-27B-Q1_0.gguf","system_fingerprint":"...","object":"chat.completion.chunk","timings":{...}}
```

### Confirmed defect (streaming path)
In `OpenAiSseRewriter.Process` (`Core/Services/OllamaProxyHandler.cs`, ~lines 1384–1415), the Move/Strip branch unconditionally executes `delta["content"] = JsonValue.Create(content);` even when the incoming delta had no content key. This injects a spurious `"content":""` into every reasoning-phase delta, deviating from the expected format.

### Design of fix
Replace the unconditional content write in the Move/Strip branch with:
- If extracted visible content is non-empty → set delta["content"] to it
- If incoming content was fully consumed as thinking → remove the content key
- If incoming content was missing/empty → leave delta untouched

---

- [x] Step 1: Create this plan file in Plans/ folder
- [x] Step 2: Empirically verify JsonNode null-serialization with WhenWritingNull
- [x] Step 3: Fix OpenAiSseRewriter.Process Move/Strip branch in OllamaProxyHandler.cs
- [x] Step 4: Verify serialization preserves null finish_reason
- [x] Step 5: Walk through both scenarios and confirm reference format
- [x] Step 6: Build solution
- [x] Step 7: Commit changes to git

## Outcome
The Move/Strip branch now sets `delta.content` only when the extractor returns visible text,
removes the key when the incoming content was fully consumed (thinking or partial-tag buffering),
and leaves the delta untouched when the upstream frame carried no content at all. The code lives in
`Services/Proxy/OllamaProxyHandler.Passthrough.cs` (`OpenAiSseRewriter.Process`) after the split out
of the old monolithic `OllamaProxyHandler.cs`; `OpenAiSseFrameParityTests` pins the behaviour.
