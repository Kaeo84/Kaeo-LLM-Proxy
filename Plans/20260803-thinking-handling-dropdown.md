# Thinking handling dropdown for model mappings

## Understanding
Replace the "Extract <think> tags" checkbox in the per-model Configure Model dialog with a
"Thinking Handling" dropdown offering: leave thinking inline in the visible answer, move it into
reasoning_content, or remove it from client output (still captured in logs). The request-side
"Enable thinking compatibility" checkbox (strip assistant response-prefill turns) stays separate.

## Steps
- [x] Extend ThinkingMode enum in Core/Models/AppSettings.cs with LeaveInline (default),
	  StripFromOutput, and MoveToReasoningContent alias for ExtractThinkTags; keep Off/ExtractThinkTags
	  as legacy aliases so persisted values keep working. Setting remains per-model (ModelMapping).
- [x] Update OpenAiSseRewriter in OllamaProxyHandler.cs to handle all modes (mirror only in
	  LeaveInline/Off; extract in ExtractThinkTags/MoveToReasoningContent; strip think blocks and
	  native reasoning_content in StripFromOutput).
- [x] Update TransformNonStreamingChatBody/CopyNonStreamingChatResponseAsync to support
	  StripFromOutput and report the raw upstream body via onBody.
- [x] Capture raw upstream bodies for logging in the passthrough collect-response-details paths
	  (SSE chat path uses a Stream.Null-backed ResponseCaptureStream fed by a new rawCapture
	  parameter; non-streaming paths log the raw body before transformation).
- [x] Replace _chkExtractThinkTags checkbox with _cmbThinkingHandling dropdown in
	  ModelMappingDialog.cs (field, property, layout row 12, labels, tooltip, load/save).
- [x] Build the solution: no C# compile errors; only MSB3027/MSB3021 because the running
	  Kaeo LLM Proxy.exe locks the output exe (close the app and rebuild to verify at runtime).
- [x] Commit changes to git.

## Notes
- The two original checkboxes did not directly conflict: thinking compatibility acts on the
  request (strips trailing assistant prefill), extraction acted on the response. The dropdown
  removes the response-side ambiguity by making the three outcomes mutually exclusive.
- StripFromOutput keeps the unmodified upstream body in captured request logs when
  CollectResponseDetails is enabled, so stripped thinking remains reviewable.
