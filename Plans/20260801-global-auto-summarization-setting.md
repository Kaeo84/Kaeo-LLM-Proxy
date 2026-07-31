# Add Global Auto-Summarization Setting

## Understanding
Add a global setting that controls whether the proxy automatically retries a chat request with
summarized context when the upstream reports a context-window overflow. Defaults to enabled.
Acts as a master switch combined with the per-mapping `ModelMapping.EnableAutoSummarization` flag.

Effective behavior = `_settings.EnableAutoSummarization && (mapping?.EnableAutoSummarization ?? true)`.

## Steps
- [x] 1. Add EnableAutoSummarization to RuntimeSettings + AppSettings (props + Create/Apply) in Core/Models/AppSettings.cs
- [x] 2. Add enable_auto_summarization column to runtime_settings schema baseline + migration in Infrastructure/AppDatabase.cs
- [x] 3. Extend LoadRuntimeSettings + SaveRuntimeSettings for enable_auto_summarization in Infrastructure/AppDatabase.cs
- [x] 4. Combine global flag with per-mapping flag in OllamaProxyHandler.HandleChatAsync
- [x] 5. Add _chkAutoSummarization checkbox to MainForm.Designer.cs (instantiate, row 10, shift rows 10-14 -> 11-15, RowCount 15 -> 16, config, backing field)
- [x] 6. Wire _chkAutoSummarization in MainForm.cs LoadSettingsToForm + BtnSaveSettings_Click
- [x] 7. Build and verify
- [x] 8. Commit
