# Redesign Model Capabilities UI with auto-detect

## Understanding
Replace the fixed checkbox list for model capabilities in the Model Mapping dialog with an editable table. Each row is a capability string with an enable/disable toggle. Add an "Auto-Detect" button that best-effort determines capabilities using **data-only** upstream metadata calls (never model invocations) and replaces the table contents. Users can also add rows via an "Add" button (pick a known capability from a dropdown or type a custom one) and remove rows.

## Assumptions
- Capabilities persist as `ModelMapping.Capabilities` (a `List<string>`); enabled rows are what get advertised.
- Custom (non-canonical) capability tokens must be preserved end-to-end, so `ModelCapabilities.Normalize` keeps unknown tokens (it previously dropped them).
- "Data calls only" = `GET /v1/models/{id}` (fallback `GET /v1/models`) plus conservative name heuristics; no chat/embedding/generation requests.
- Follow the existing grid convention (MainForm `_dgvMappings`): `AllowUserToAddRows=false`, Fill columns, no row headers, plus a button row (Add/Remove) rather than an in-grid new-row.

## Approach
1. Make `ModelCapabilities.Normalize` keep known tokens in canonical order and append custom tokens (deduped, original order) so custom capabilities survive.
2. Add a `CapabilityDetector` static service (main project) that fetches model metadata (data-only) and returns detected tokens + a human-readable summary.
3. In `ModelMappingDialog`, swap the checkbox group for a `DataGridView` (Capability combo column with DropDown style + Enabled checkbox column) inside the existing "Model Capabilities" group, with an Auto-Detect / Add / Remove button row and a status label.
4. Rework the `Capabilities` property to read/write the grid; add the three button handlers; extract a `ResolveApiKey()` helper and reuse it in the existing fetch/model-info handlers.

## Key Files
- `Kaeo LLM Proxy Core/Models/ModelCapabilities.cs` — Normalize must preserve custom tokens.
- `CapabilityDetector.cs` (new, main project) — best-effort data-only detection.
- `ModelMappingDialog.cs` — UI + property + handlers.

## Risks & Open Questions
- Most providers expose no capability metadata, so auto-detect is largely heuristic and advisory (surfaced in the status label). Matches the user's "best-effort" ask.
- The capabilities group needs a fixed height (grids don't auto-size); set the group to a fixed size inside the AutoSize table row.
- `DataGridViewComboBoxColumn` has no `DropDownStyle` property and its built-in cell is select-only, so the cell is made editable via the `EditingControlShowing` event (swap in a `DropDown`-style combo).

## Steps
- [x] Modify `ModelCapabilities.Normalize` to preserve custom tokens after the canonical ones.
- [x] Create `CapabilityDetector.cs` with `DetectAsync` + `CapabilityDetectionResult` (data-only fetch, explicit-metadata parse, name heuristics, text+chat fallback).
- [x] Update `ModelMappingDialog` field declarations: drop `_tlpClientCapabilities`/`_capCheckboxes`; add grid, button panel, Auto-Detect/Add/Remove buttons, status label, group table.
- [x] Replace the `Capabilities` property to read/write the grid rows.
- [x] Replace the capabilities UI construction (checkbox loop → grid + buttons + status).
- [x] Add `BtnCapAutoDetect_Click`, `BtnCapAdd_Click`, `BtnCapRemove_Click`, `SetCapStatus`, `DgvCapabilities_EditingControlShowing`, and a `ResolveApiKey()` helper.
- [x] Refactor `BtnFetchModels_Click` and `BtnModelInfo_Click` to use `ResolveApiKey()`.
- [x] Build the project and fix any compile errors.
- [x] Commit the change.

## Notes
- Build compiles with zero C# errors. A running (elevated) app instance was locking the output DLLs, so only the post-compile copy step reported MSB3027/MSB3021; it succeeds once the app is closed.
- Committed as `95aa144`.
