# Fix Configure Model dialog settings not persisting

## Understanding
Changes made in the "Configure Model" dialog (ModelMappingDialog) were not all persisted.
Reported: Temperature Priority and Repeat Penalty Priority definitely lost; Vision Support
and Synthesize OpenAI metadata appeared lost too. Every ModelMapping property must survive
Save → restart.

## Root Causes
The save chain is: dialog → row.Tag mapping → TryCommitMappings → AppDatabase (SQLite is the
only store; AppSettings.ModelMappings is [JsonIgnore]). Three independent drop points:

1. **AppDatabase** — `temperature_priority` and `repeat_penalty_priority` columns did not
   exist anywhere (schema, migration, INSERT, SELECT, parameter binding, reader). Priorities
   reset to Client App Priority on every app restart.
2. **MainForm.TryCommitMappings** — rebuilt each mapping with a hand-written initializer that
   omitted `SynthesizeOpenAiMetadata`, resetting it to false on every Save.
3. **MainForm.LoadSettingsToForm** — rebuilt each grid row's Tag with a hand-written copy that
   omitted `SupportsVision` and `SynthesizeOpenAiMetadata`; the next grid commit persisted the
   wiped values (also hit on app start and after instruction-set edits).

ModelMappingDialog.ShowConfigureDialog itself already writes all 24 persisted properties back
to the mapping on OK — verified, no change needed there.

## Steps
- [x] Add priority columns to AppDatabase schema, migration, and save/load paths
  - CREATE TABLE baseline: `temperature_priority`, `repeat_penalty_priority`
	(INTEGER NOT NULL DEFAULT 0); also added missing `synthesize_openai_metadata` to baseline
  - MigrateModelMappingsTable: ALTER TABLE ADD COLUMN for both priority columns
  - SaveModelMappings INSERT + AddModelMappingParameters bindings
  - LoadModelMappings SELECT + ReadModelMapping (ordinals 22/23, Enum.IsDefined guards,
	unknown values fall back to SamplingPriority.ClientApp)
- [x] Fix MainForm.TryCommitMappings to clone the row Tag mapping
  (`advanced?.Clone() ?? new ModelMapping()` + override the four grid-managed fields:
  ProxyName, ModelName, UpstreamUrl, UpstreamType)
- [x] Fix MainForm.LoadSettingsToForm to set `row.Tag = mapping.Clone()`
- [x] Build verification (compile clean; full build to unlocked output folder succeeded)
- [x] Git commit

## Notes
- Default column value 0 = SamplingPriority.ClientApp, matching prior in-memory defaults, so
  existing databases read back with unchanged behavior until the user edits a mapping.
- Using `ModelMapping.Clone()` in both MainForm paths makes the copies future-proof: new
  properties added to ModelMapping (and its Clone method) can no longer be silently dropped.
