# Per-Model reasoning_effort: Value List, Selection, Priority, and Proxy Application

## Understanding
Add per-model `reasoning_effort` configuration: an ordered (priority) list of allowed values
entered as a comma-separated field, a dropdown to select which value to use with the model, and
a SamplingPriority-style dropdown (Client App / Proxy / Provider) controlling how the proxy
applies it when forwarding OpenAI-compatible requests. Known-model profiles prefill the list
when the model name matches and nothing is configured yet.

## Known Model Profiles (prefilled)
- DeepSeek-V4 (deepseek-v4-pro/flash) and GLM series (glm-5.x): `high, max` — default `high`
- Kimi K3 (kimi/kimi-k3): `max` — default `max`
- Qwen3.8-Max: `xhigh, medium, low` — default `xhigh`

## Steps
1. [x] Add reasoning effort properties to ModelMapping and update Clone in AppSettings.cs
   - `ReasoningEffortPriority` (SamplingPriority, default ClientApp), `ReasoningEffort`
	 (selected value), `ReasoningEffortValues` (ordered List<string>)
2. [x] Create Core/Models/ReasoningEffortProfiles.cs with known-model profiles
   - `TryGetProfile(modelName, ...)` matcher + `StandardValues` vocabulary
3. [x] Extend AppDatabase schema, migration, SELECT/INSERT, ReadModelMapping, and AddModelMappingParameters
   - `reasoning_effort_priority`, `reasoning_effort`, `reasoning_effort_values` columns;
	 values list stored comma-separated
4. [x] Apply reasoning_effort in OllamaProxyHandler and add the field to LlamaCppChatRequest
   - NormalizeRequestBody: Provider drops / Proxy overrides or injects / ClientApp passes through
   - /api/chat translation path injects via ResolveReasoningEffort (Proxy priority only)
5. [x] Add reasoning effort controls, layout, behaviors, and prefill to ModelMappingDialog
   - Priority dropdown + comma-separated values field + editable selection dropdown;
	 Provider priority disables value controls; prefill on load and on model-name change
6. [x] Build the solution and fix any errors
7. [x] Save the plan file to Plans/ and commit to Git
