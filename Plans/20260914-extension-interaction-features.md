# 20260914 - Extension interaction features: inline approvals, stop, redirects

## Understanding
Three tool-window interaction gaps block the agent workflow today:

1. **Tool permission prompts block the GUI.** `ToolWindowViewModel.SendAsync` wires
   `AgentEvents.RequestPermission` to `VS.MessageBox.ShowConfirmAsync` - a modal dialog.
   While it is up the user cannot read earlier output, cannot stop the turn, and cannot
   send follow-ups; the transcript looks frozen mid-tool-call. The runtime itself is
   already async (the loop awaits the task), so only the *presentation* blocks.
2. **No stop control.** `ToolWindowViewModel.Cancel()` exists and `AgentRuntime` honors
   the cancellation token, but nothing in the UI invokes it. The Send button disables
   itself during streaming and the Enter shortcut silently swallows input.
3. **No mid-run steering.** Users of modern agent UIs expect that typing while a turn is
   running injects a "redirect" message into the running conversation instead of being
   lost. Today busy-time input is dropped.

## Design

**Stop/Send one-button toggle.** `ToolWindowViewModel` exposes `IsBusy` (set true when a
turn's `CancellationTokenSource` is created, false when disposed). The single
`SendButton` restyles via a DataTrigger on `IsBusy`: Content "Send" / "Stop", glyph
changes, tooltip "Stop the current turn". Click routing: busy -> `Cancel()`; idle ->
send. The Enter/preview-key handler stops swallowing input while busy and routes through
the same path (see redirects), so no fourth control is needed.

**Inline permission cards.** `RequestPermission` no longer shows any modal. The handler
appends a transcript line `Kind="permission"` whose `Text` summarizes tool + arguments,
carrying a `TaskCompletionSource<bool>` and an `IsAnswered` flag. The data template
renders Allow / Always allow / Deny buttons while `!IsAnswered`; clicking resolves the
TCS, writes the chosen outcome back into the line's `Text` ("Allowed", "Denied",
"Always allowed"), and collapses the buttons. "Always allow" sets a session-scoped
`_alwaysAllowThisSession` flag: the VM answers subsequent permission lines
immediately-allowed without adding cards, and appends a single status line so the user
knows the mode flipped. Mode pill semantics stay authoritative: cards only appear in
Interactive mode (Bypass/AutoPilot auto-approve as today). This mirrors the Copilot SDK
permission-handler pattern (per-call consent, session grants) the runtime already claims.

**Redirect queue.** `SendAsync(prompt)` becomes: if `IsBusy`, enqueue the text, append
`Kind="redirect"` line to the transcript, return; else run the turn as today.
`AgentEvents` gains `Func<IReadOnlyList<string>>? DrainRedirects`; `AgentRuntime` calls
it at the top of every tool-loop iteration and appends each pending string as a user
`ChatMessage` *before* building the next request - so redirects land at a clean turn
boundary, in order, ahead of the next model call. AutoPilot recursion passes the same
events bag, so the hook survives continuations. When a turn ends with the final answer
and redirects are still queued, the VM re-runs `SendAsync(string.Empty)`-style: it pops
one queued redirect and starts a fresh turn with it as the prompt (the rest stay queued
behind it), so nothing typed during a run is ever silently lost.

**Transcript/UX details.** `redirect` lines render italic and gray with their `Kind` label
already shown by the template; `permission` cards get an amber `Kind` label while pending.
`TranscriptFormatter` treats `user`, `assistant` and `redirect` as conversation content
(redirects export labeled "You (redirect)"); permission cards and tool/status lines stay
excluded from exports as execution meta. `Copy Selected` follows the same filter.

## Steps
- [x] 1. VM: IsBusy + Stop/Send button trigger + click routing + Enter no longer swallowed
- [x] 2. VM: redirect queue + DrainRedirects event hook + AgentRuntime per-iteration drain + post-turn flush of leftovers
- [x] 3. VM+XAML+code-behind: inline permission card (TCS, Allow/Always/Deny, session always-allow, answered collapse)
- [x] 4. TranscriptFormatter: kind labels for permission/redirect
- [x] 5. Build extension + full test suite green
- [ ] 6. Manual: Interactive mode approval card incl. Always; stop mid-stream; redirect mid-tool-loop lands next iteration

## Verification
F5: send a prompt that triggers a tool call (Agent/Interactive) -> card appears, UI stays
responsive (scroll, resize); "Always allow" answers this and later calls; Deny -> model
gets "Permission denied by user." mid-run Enter -> "↪ redirect" line appears, next
iteration's request carries it as a user message (check proxy request log). Stop ->
stream ends, button flips back, "[cancelled]" line; queued redirects then auto-flush.
