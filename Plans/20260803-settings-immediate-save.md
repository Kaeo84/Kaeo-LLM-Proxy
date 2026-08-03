# Settings: listener group with save button; everything else saves immediately

## Understanding
Split the Settings tab so Listen Port / Listen Address live in their own "Listener" group with a
Save button (they require a proxy restart), while all other Settings tab options persist
immediately when changed. The Model Mappings dialog OK button becomes "Save" and mapping changes
persist as soon as they are made.

## Steps
- [x] Add _grpListener/_tlpListener/_btnSaveListener to MainForm.Designer.cs; move port/address
	  controls into the group; remove the global "Save Settings" button; reflow rows 0-13.
- [x] MainForm.cs: BtnSaveListener_Click validates and saves port/address with a restart reminder.
- [x] Immediate-save handlers: SaveGeneralSettings (checkboxes + Max Log Entries) and
	  SaveLoggingSettings (logging group), wired via CheckedChanged/SelectedIndexChanged/Validated
	  in the constructor, guarded by _loadingSettings during LoadSettingsToForm.
- [x] Extracted PersistSettingsCore (credentials encryption + DB saves + settings.Save +
	  handler.UpdateSettings) and TryCommitMappings/CommitMappingsFromGrid; mapping add/configure/
	  remove/duplicate now persist immediately, with a warning shown when the grid is invalid.
- [x] ModelMappingDialog OK button text changed to "Save" (changes apply on click as before).
- [x] Build: no C# compile errors; only MSB3027/MSB3021 because the running Kaeo LLM Proxy.exe
	  locks the output exe (close the app and rebuild to verify at runtime).
- [x] Commit changes to git.

## Notes
- Heartbeat settings (Heartbeats tab) keep their existing Save button; they were not part of the
  Settings tab request.
- Credentials/instruction sets continue to persist through their dialogs and are included in every
  PersistSettingsCore pass.
