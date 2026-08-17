# Per-Model Reasoning Effort Payload Format (Legacy / Modern / Both / Qwen Cloud)

## Understanding

Different providers expect reasoning effort in different wire shapes:

- OpenAI legacy models (e.g. o3-mini): top-level `"reasoning_effort": "low"`
- Newer OpenAI models (e.g. gpt-5.5): nested `"reasoning": { "effort": "high" }`
- Qwen Cloud: `"enable_thinking": true` + `"reasoning_effort": "medium"`
  (from `extra_body={"enable_thinking": True, "reasoning_effort": "medium"}`)

Add a per-model "Reasoning Effort Format" dropdown (Legacy / Modern / Both / Qwen Cloud) that
controls how the proxy injects the configured effort value. The format applies only under
**Proxy priority** (the only mode that injects), and injected values are lowercased because
OpenAI-style providers expect lowercase.

## Format semantics (Proxy priority injection)

| Format    | Payload emitted                                                            |
|-----------|----------------------------------------------------------------------------|
| Legacy    | `"reasoning_effort": "<value>"` (current behavior, default)               |
| Modern    | `"reasoning": { "effort": "<value>" }`                                    |
| Both      | both of the above                                                          |
| QwenCloud | `"enable_thinking": true` + `"reasoning_effort": "<value>"`               |

- Client App priority keeps passing client `reasoning_effort` / `reasoning` / `enable_thinking`
  through unchanged; Provider priority keeps dropping only `reasoning_effort` as before.
- Applies to both the /v1 passthrough (`NormalizeRequestBody`) and the /api/chat translation
  (`LlamaCppChatRequest`).

## Steps

- [x] 1. Add ReasoningEffortFormat enum and ModelMapping property in AppSettings.cs (incl. Clone)
- [x] 2. Extend LlamaCppChatRequest in OllamaTypes.cs (LlamaCppReasoning, EnableThinking)
- [x] 3. Implement format-aware injection in OllamaProxyHandler.NormalizeRequestBody (lowercased)
- [x] 4. Replace ResolveReasoningEffort with format-aware application in the /api/chat path
- [x] 5. Persist reasoning_effort_format in AppDatabase.cs (schema, migration, load/save)
- [x] 6. Add Reasoning Effort Format dropdown to ModelMappingDialog.cs (Proxy-only enablement)
- [x] 7. Extend ReasoningEffortNormalizationTests with format and lowercase tests
- [x] 8. Build solution and run tests
- [ ] 9. Git commit
