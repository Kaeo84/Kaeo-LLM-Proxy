# Per-model sampling priority for Temperature and Repeat Penalty

## Understanding
Replace the SendTemperature/SendRepeatPenalty checkboxes with a three-way priority per field:
Client App Priority (client value wins), Proxy Priority (configured proxy value overrides the
client), Provider Priority (field omitted so the provider's platform setting wins). Each priority
control sits directly above the value it governs in the Configure Model dialog.

## Steps
- [x] Add SamplingPriority enum (ClientApp/Proxy/Provider) in Core/Models/AppSettings.cs; replace
	  SendTemperature/SendRepeatPenalty with TemperaturePriority/RepeatPenaltyPriority on
	  ModelMapping (default ClientApp) and Clone().
- [x] OllamaProxyHandler: ResolveSamplingValue helper applied in /api/generate and /api/chat
	  upstream builders (Proxy sends configured value, Provider omits, ClientApp passes client).
- [x] NormalizeRequestBody applies priorities to passthrough /v1 bodies: Provider strips client
	  temperature/repeat_penalty; Proxy overwrites or injects the configured values; ClientApp
	  passes through untouched.
- [x] Configure Model dialog: Temperature Priority and Repeat Penalty Priority dropdowns placed
	  directly above the Temperature and Repeat Penalty inputs; inputs disabled under Provider
	  Priority; load/save wired; SamplingPriorityOption display record.
- [x] MainForm: grid row Tag copy and TryCommitMappings carry the priorities; Test Console omits
	  or overrides values per priority and disables inputs under Provider Priority.
- [x] Build: no C# compile errors; only MSB3027/MSB3021 because the running Kaeo LLM Proxy.exe
	  locks the output exe.
- [x] Commit changes to git.

## Notes
- ClientApp with no client value omits the field (provider default applies).
- Auto-summarization still sends its own temperature 0.3 for reliable summaries.
