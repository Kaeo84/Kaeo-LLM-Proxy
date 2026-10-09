# Reasoning Effort Format: Multi-Select Flags + Qwen Cloud extra_body

## Understanding

Two corrections to the reasoning effort format feature:

1. The Qwen Cloud option must emit `"extra_body": { "enable_thinking": true, "reasoning_effort": "..." }` —
   a wrapped object, not top-level fields.
2. The format setting becomes a multiselect so any combination of wire shapes can be emitted,
   which makes the dedicated "Both" option obsolete.

## Design

- Flags enum: Legacy=1, Modern=2, QwenCloud=4, ChatTemplateKwargs=8; Both removed; default Legacy.
- DB column stays INTEGER, now a bitmask. Base schema DEFAULT 1; read masks to known bits and
  falls back to Legacy when zero. No legacy value-mapping (unreleased app).
- Proxy-priority takeover manages four representations: `reasoning_effort`, `reasoning`,
  `extra_body`, `chat_template_kwargs` — each overridden when its flag is selected, dropped
  otherwise. Client top-level `enable_thinking` passes through (no longer proxy-managed).
- UI: CheckedListBox (CheckOnClick) replaces the dropdown; enabled only under Proxy priority.
  Nothing-checked degrades to Legacy.

## Steps

- [x] 1. Convert ReasoningEffortFormat to flags enum and update ModelMapping docs in AppSettings.cs
- [x] 2. Swap EnableThinking for ExtraBody on LlamaCppChatRequest in OllamaTypes.cs
- [x] 3. Rework OllamaProxyHandler: HasFlag injection, extra_body writer, remove enable_thinking branch
- [x] 4. Update AppDatabase defaults and bitmask read
- [x] 5. Replace the dialog format dropdown with a CheckedListBox multiselect
- [x] 6. Update and extend the normalization tests for extra_body and multiselect
- [x] 7. Build, run tests, commit, and push
