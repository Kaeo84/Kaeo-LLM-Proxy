# 20260914 - Proxy GUI feature-sync plan

## Understanding
The proxy's WinForms shell (MainForm log grid, ModelMappingDialog settings, status/dashboard) predates the
current feature set: the MEAI adapter project (Phase B5), the OpenAI-passthrough declared-tool sanitizer, the
merged system-message behavior, per-model thinking/reasoning options, and the VS extension's new
ReasoningSource / display-preference model. The GUI still surfaces the older vocabulary. This document plans
catching the proxy GUI up, sequenced with Phase-B execution rather than as a separate big-bang redesign.

## Gap review
1. **Sanitizer visibility.** The passthrough sanitizer strips/keeps tool calls the client did not declare, and
   the system-merge collapses leading system messages. Neither is visible anywhere except raw request/response
   capture. The mapping's existing DebugSummary/notes column is the natural host: emit one-line notes
   ("tools dropped: get_weather, delete_repo"; "system messages merged: 2->1") and ensure the log grid's detail
   view shows them.
2. **Thinking vocabulary drift.** Proxy per-mapping `ThinkingMode` (LeaveInline / MoveToReasoningContent /
   StripFromOutput) and the extension's `ModelEntry.ReasoningSource` (Auto/ThinkingField/InlineTags/Disabled)
   describe the same axis in two dialects. When B2/B3 IR-ize the pipeline, align the dialog copy (or a shared
   enum in the adapter project) so users see one concept, not two.
3. **UpstreamKind (B4).** ModelMappingDialog needs an Upstream selector (llama.cpp/Ollama/OpenAI/...) mirroring
   the extension's Connections tab once provider clients land; today only UpstreamUrl exists.
4. **Reasoning token accounting.** Token stats already capture reasoning tokens where reported; the dashboard
   should surface "thinking delivered vs stripped" once the ThinkingMode is a serializer option (B3), so
   per-model policy outcomes are inspectable.
5. **Conformance status (B6).** The API Specs conformance harness needs a results surface - a "Compliance" tab
   listing spec-validated endpoints with pass/fail, filtered per provider folder (openai/gemini/mistral/cohere).
6. **Extension parity hints.** The proxy log can tag requests originating from the VS extension (User-Agent or
   a header the extension already/should send) so debugging sessions can filter to extension traffic.

## Design
- Add an `ExtensionNotes` string to the passthrough transform pipeline (same mechanism as the existing
  per-request debug notes on other paths); render in the existing detail view - no new columns required beyond
  optionally widening the grid column set (keep the grid lean; details live in the popup).
- Introduce a shared `ThinkingSource` display name table in Kaeo LLM Proxy VS Extension.Core so proxy dialog
  text and the extension combo labels derive from one enum (prevents further drift).
- Defer 3-5 until their parent Phase-B steps land (B4 for UpstreamKind, B3 for policy outcomes, B6 for the
  Compliance tab) - this document is the queue that keeps them from being forgotten.

## Steps
- [ ] 1. Emit sanitizer/system-merge notes into the passthrough debug summary; surface in log detail view
- [ ] 2. Shared thinking-source naming table in the adapter project; align both dialogs' copy
- [ ] 3. (with B4) UpstreamKind selector + per-upstream defaults in ModelMappingDialog
- [ ] 4. (with B3) Dashboard "thinking delivered vs stripped" per-model stat
- [ ] 5. (with B6) Compliance tab: spec-validation results grouped by provider folder
- [ ] 6. Extension-traffic tagging in the request log

## Verification
Run the proxy with DebugMode + a Copilot + an extension client against a thinking model; confirm notes,
reasoning stats, and (post-B4/B6) the new selectors/tabs render and filter correctly.
