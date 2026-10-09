# Log Fetch Models (and app-initiated upstream calls) in the request log

## Understanding
The Configure Model dialog's "Fetch Models" button issued a GET /v1/models to the upstream but
never recorded it in the request log. Every application-initiated request/response should appear
in the log like completions (without tokens). The Test Console already logged; Fetch Models was
the remaining gap.

## Steps
- [x] Make RedactedBodyText and RedactSensitiveJsonFields internal in OllamaProxyHandler.cs.
- [x] Rework FetchUpstreamModelsAsync to return UpstreamModelFetchResult (models, status code,
	  raw body, error) instead of swallowing outcomes.
- [x] Add AppSettings/StatisticsService? parameters to ShowConfigureDialog; BtnFetchModels_Click
	  times the call and logs it via LogFetchModelsRequest with per-model redaction
	  (RedactResponseBodies / RedactSensitiveJsonFields) gated by CollectResponseDetails.
- [x] Update both MainForm.ShowConfigureDialog call sites to pass _settings and _stats.
- [x] Build: no C# compile errors; only MSB3027/MSB3021 because the running Kaeo LLM Proxy.exe
	  locks the output exe (close the app and rebuild to verify at runtime).
- [x] Commit changes to git.

## Notes
- Log entries use Method GET, OllamaPath "(fetch models)", UpstreamPath "/v1/models", with no
  token fields populated, matching the user's expectation that non-completion calls have no tokens.
- Failures (non-success status, unparseable body, exceptions) are logged as Error entries with the
  upstream body or exception message, and the response body is still captured when available.
