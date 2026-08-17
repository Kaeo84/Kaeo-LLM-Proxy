# Test Console: Show the Transformed (Upstream) Request in Logs

## Understanding

The Test Console builds its own payload and sends it straight to the upstream, bypassing
`OllamaProxyHandler.NormalizeRequestBody`. As a result, test-console requests never get
`reasoning_effort` (or system-message merging / model rewriting) applied, and
`RequestLog.UpstreamRequestBody` is never populated - so the Log Details window shows no
"Upstream Request Body" tab for test-console entries and the transformed request is invisible.

## Approach

1. Make `OllamaProxyHandler.ShouldApplyThinkingCompatibility` and `RedactRequestBodyForLog`
   `internal static` (taking `AppSettings`) so the test console reuses the exact helpers the
   proxy paths use; update the handler's internal call sites.
2. Rework `MainForm.BtnTestSend_Click` to act like a regular client app:
   - payload carries the proxy model name and always includes the console's temperature /
	 repeat_penalty as client values,
   - drop the manual instruction-set injection (normalization injects it),
   - run the serialized client body through `OllamaProxyHandler.NormalizeRequestBody`,
   - send the normalized body upstream,
   - capture `RequestBody` (client original) and `UpstreamRequestBody` (normalized) via
	 `RedactRequestBodyForLog`, mirroring `PassthroughAsync`.
3. Validate with a build and the existing unit tests, then commit.

## Steps

- [x] 1. Make ShouldApplyThinkingCompatibility internal static; update call sites
- [x] 2. Make RedactRequestBodyForLog internal static; update call sites
- [x] 3. Rework test console payload/log creation in MainForm to use the shared pipeline
- [x] 4. Build solution and run tests
- [x] 5. Git commit
