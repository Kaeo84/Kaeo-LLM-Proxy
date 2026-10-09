# Code Vector Store: Honor DefaultTopK + Settings Help (Modal & Tooltips)

## Context
- `code_search` MCP tool hardcodes `topK = 5`, ignoring the `DefaultTopK` setting (currently 8 in the app).
- No settings documentation: config page has no help link; `CodeVectorHelpPage` only documents tools.

## Plan
- [x] Fix `CodeSearch` in `CodeVectorTools.cs` to honor `DefaultTopK` (nullable `topK` param, updated description).
- [x] Create `CodeVectorSettingsHelp.cs` with descriptions for every setting + `BuildText()`.
- [x] Create `CodeVectorSettingsHelpDialog.cs` modal Form.
- [x] Wire the Help link + tooltips into `CodeVectorConfigPage.cs`.
- [x] Append settings documentation to `CodeVectorHelpPage.cs`.
- [x] Build the solution and fix any errors.
- [x] Git commit with a summary message.

## Verification
- `code_search` without `topK` now returns 8 results (honors the DefaultTopK setting).
- Build successful; app restarted with updated module; MCP server back on port 8388.

## Notes
- Descriptions live in a single shared static class (`CodeVectorSettingsHelp`) so the modal, tooltips, and tab help stay consistent.
- Modal follows the existing `ModelInfoDialog` pattern.
- `topK` becomes `int?` in the MCP schema; null = use the store's Default Top K setting.
