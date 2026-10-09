# Add Central Credential Store with GUI Tab

## Understanding
Add a central credential store: a named list of API keys/secrets persisted encrypted in the SQLite
AppDatabase, manageable via a new Credentials tab, and consumable by model mappings through a
dropdown in the ModelMappingDialog (pick a stored credential instead of typing a key).

## Steps
- [x] 1. Add `StoredCredential` model, `AppSettings.Credentials` list, `ModelMapping.CredentialName` (+Clone), and `FindCredential`/`ResolveApiKey` helpers in Core/Models/AppSettings.cs
- [x] 2. Add `credentials` table, `LoadCredentials`/`SaveCredentials`, schema migration, and `credential_name` column (model_mappings) in Infrastructure/AppDatabase.cs
- [x] 3. Load and decrypt credentials at startup in Program.cs (extend ResolvePassphrase/TryDecrypt to cover credentials)
- [x] 4. Create CredentialDialog.cs modal (name + secret editor with show/hide)
- [x] 5. Add the Credentials tab controls and layout to MainForm.Designer.cs (ListView + Add/Edit/Remove buttons, backing fields)
- [x] 6. Implement Credentials tab handlers in MainForm.cs (refresh, add, edit, remove with rename/remove propagation) and persist/encrypt credentials in BtnSaveSettings_Click via a shared EnsurePassphrase helper
- [x] 7. Add the credential dropdown to ModelMappingDialog.cs and wire CredentialName through ShowConfigureDialog and its MainForm call sites
- [x] 8. Resolve the effective API key (credential-aware) in OllamaProxyHandler.cs upstream auth/heartbeats and the MainForm test console
- [x] 9. Build, fix any compile errors, and git commit
