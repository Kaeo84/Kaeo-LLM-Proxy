# Per-model disable for Temperature and Repeat Penalty

## Understanding
Hosted/web-based models may have sampling values configured on their platform; the proxy should not
override them unless the user opts in per model. Two checkboxes in the Configure Model dialog
control whether temperature / repeat_penalty are included in upstream requests at all.

## Steps
- [x] Add ModelMapping.SendTemperature / SendRepeatPenalty (default true) in Core/Models/AppSettings.cs
	  and carry them through Clone().
- [x] Gate temperature/repeat_penalty in the Ollama-native /api/generate and /api/chat upstream
	  request builders (fields omitted from JSON when disabled; nulls are ignored by the
	  WhenWritingNull serializer).
- [x] Configure Model dialog: two checkboxes above "Enable this proxy model" that also enable/
	  disable the Temperature and Repeat Penalty numeric inputs; load/save wired.
- [x] Test Console: payload built as JsonObject omitting disabled fields; numeric controls
	  disabled per selected model.
- [x] Carry the flags through MainForm grid row Tag copy (LoadSettingsToForm) and
	  TryCommitMappings rebuild.
- [x] NormalizeRequestBody drops client-supplied temperature/repeat_penalty from passthrough
	  /v1/* bodies when the flags are disabled, so hosted providers keep platform values.
- [x] Build: no C# compile errors; only MSB3027/MSB3021 because the running Kaeo LLM Proxy.exe
	  locks the output exe.
- [x] Commit changes to git.

## Notes
- Auto-summarization intentionally still sends its own temperature 0.3 for reliable summaries.
