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

- [ ] Step 1: Create this plan file in Plans/ folder
- [ ] Step 2: Empirically verify JsonNode null-serialization with WhenWritingNull
- [ ] Step 3: Fix OpenAiSseRewriter.Process Move/Strip branch in OllamaProxyHandler.cs
- [ ] Step 4: Verify serialization preserves null finish_reason
- [ ] Step 5: Walk through both scenarios and confirm reference format
- [ ] Step 6: Build solution
- [ ] Step 7: Commit changes to git
