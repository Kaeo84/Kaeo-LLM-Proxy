# Kaeo VS Extension — Code Review Fix Plan

Derived from the Code Review Report. Each step maps to a report section.
Status legend: `[ ]` pending, `[x]` done (with a Note on what changed).

## Step 1 — Surface HTTP error bodies + always-on error logging (§2)
- [x] `Core\OllamaChatClient.cs`: on non-success status, read the body and throw a descriptive `InvalidOperationException` instead of bare `EnsureSuccessStatusCode()`.
- [x] `Core\DebugLog.cs`: always record `Error` lines; gate only `Verbose`/`Info` on `Debugger.IsAttached`.

**Note:** Replaced `response.EnsureSuccessStatusCode()` with an explicit `IsSuccessStatusCode` check that reads the body via `ReadAsStringAsync` and throws `InvalidOperationException` carrying `"{status} {reason} from {uri}: {truncated body}"` (body truncated to 2000 chars via a new `Truncate` helper). `DebugLog.Write` now skips the `IsEnabled` gate only for `ERROR` lines, so a failed send is logged even without a debugger attached.

## Step 2 — Fix final-answer duplication + reasoning drop (§3, §13)
- [x] `ToolWindow\ToolWindowViewModel.cs`: only assign `streaming.Text = result.FinalText` when nothing was streamed into that line.

**Note:** Restructured `RunTurnAsync` so the single `streaming` assistant line is created *before* the `AgentEvents` are built, and `TextDelta`/`ReasoningDelta` now append explicitly to that line (`streaming.Text += delta`, `streaming.Reasoning += delta`) instead of "the last line". The final assignment is guarded: `if (streaming.Text.Length == 0 && result.FinalText.Length > 0)`. Removed the now-dead `AppendDelta`/`AppendReasoning` helpers. This fixes both the duplicated/orphaned placeholder on tool-call turns and the dropped reasoning that arrived right after a tool line.

## Step 3 — Honor per-model enable flag (§4)
- [x] `ToolWindow\ToolWindowViewModel.cs`: skip models whose `ModelEntry.Enabled == false`.

**Note:** In `LoadCoreAsync`, the per-model `entry` lookup (already needed for `ReasoningSource`) is now reused; `if (entry is { Enabled: false }) continue;` excludes disabled models from the dropdown. `ReasoningSource` is read from the same `entry` to avoid a second lookup.

## Step 4 — Apply saved default mode on load (§5)
- [x] `ToolWindow\ToolWindowViewModel.cs`: apply `Defaults.Mode` when valid, mirroring `Defaults.Agent`.

**Note:** Added a `savedDefaultMode` check right after the existing `savedDefaultAgent` block: `if (!string.IsNullOrEmpty(savedDefaultMode) && Modes.Any(m => m == savedDefaultMode)) CurrentMode = savedDefaultMode!;` An unknown value falls back to the ctor default (`Interactive`).

## Step 5 — MCP tool-name routing on ambiguous server names (§6)
- [x] `Core\McpServerManager.cs`: route by exact `server.Name-tool.Name` match (longest-first) before falling back, so names containing `-` resolve correctly.

**Note:** Replaced the first-dash split in `ExecuteToolAsync` with a loop over enabled servers ordered by `Name.Length` descending, matching the full `"{server.Name}-"` prefix and verifying the remainder is a known tool name on that server. A bare (unprefixed) tool-name fallback follows. Server names containing `-` now resolve to the correct server instead of mis-routing on the first dash.

## Step 6 — Reuse HttpClient for the streaming client (§7, partial)
- [x] `ToolWindow\ToolWindowViewModel.cs`: cache one `OllamaChatClient` per `(baseUrl, model)` instead of newsing per turn.

**Note:** Added a `_clients` `Dictionary<string, OllamaChatClient>`; `ResolveCurrentModel` now keys on `"{baseUrl}|{model}|{apiKey}|{reasoningSource}"` and reuses the cached client instead of constructing a new `HttpClient` each turn. (Other per-call `HttpClient` sites — `OllamaApiClient`, `McpServerManager.CreateHttpClient` — are left as-is; a full shared-client refactor is a larger change best done separately.)

## Step 7 — Non-blocking stream reads (§8)
- [x] `Core\OllamaChatClient.cs`: use `StreamReader.ReadLineAsync` with explicit `Encoding.UTF8`.

**Note:** `new StreamReader(stream, Encoding.UTF8)` and the loop is now `while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)`, so a thread-pool thread is no longer parked for the whole generation. `CancellationToken` is still honored between lines by the existing `ThrowIfCancellationRequested` (net48's `ReadLineAsync` has no token overload).

## Step 8 — Preserve BaseUrl path prefix (§9)
- [x] `Core\OllamaChatClient.cs` + `Core\OllamaApiClient.cs`: build endpoint URLs without discarding a path in the base.

**Note:** Added `BuildEndpointUri(endpointPath)` = `new Uri(new Uri(_baseUrl.TrimEnd('/') + "/"), endpointPath.TrimStart('/'))` to both clients. All endpoint calls (`/api/chat`, `/api/tags`, `/health`) now route through it, so a base like `https://host/kaeo` correctly becomes `https://host/kaeo/api/chat` instead of `https://host/api/chat`.

## Step 9 — Protect settings from destructive overwrite on load failure (§10)
- [x] `Core\ExtensionSettingsStore.cs`: track load failure and refuse a save that would wipe real settings.

**Note:** Added a `_loadFailed` flag: cleared on a successful `LoadAsync`, set when the file exists but could not be read/parsed (the fallback-to-defaults path). `SaveAsync` now, when `_loadFailed` and the file exists, copies it to `settings.jsonc.bak-<timestamp>` before overwriting, so real settings are recoverable rather than silently wiped. (A hard refuse-to-save was avoided so a transient parse glitch can't permanently block the settings UI.)

## Step 10 — Remove committed backup file (§15, partial)
- [x] Delete `Kaeo VS Extension\Kaeo VS Extension.csproj.Backup.tmp`.

**Note:** Deleted the file. `git ls-files` confirmed it was not tracked, so no `git rm` was needed.

## Step 11 — Register Core assembly codeBase in pkgdef (§1, partial)
- [x] `Kaeo VS Extension.csproj`: add a `dependentAssembly` codeBase entry for `Kaeo LLM Proxy VS Extension.Core` so it resolves deterministically in the net48 host.

**Note:** `VsixPkgDefFile` is not a real VSSDK property (verified against `Microsoft.VsSDK.targets`), so instead: added `KaeoCore.pkgdef` (a fragment with the `codeBase` section, whose section name carries the `KAEoCorePkgDefSection` marker) and an `AppendCorePkgDef` MSBuild target (`AfterTargets="GeneratePkgDef"`) that appends the fragment to `$(IntermediateOutputPath)$(TargetName).pkgdef` — the file the VSIX actually ships (line 473 uses `IntermediateOutputPath`, not `OutDir`). The append is idempotent (marker check) and runs before `CreateVsixContainer`. Verified: the built VSIX's `Kaeo VS Extension.pkgdef` now contains the Core `codeBase` entry, and the VSIX payload includes `Kaeo LLM Proxy VS Extension.Core.dll`, `Kaeo VS Extension.dll`, `System.Text.Json.dll`, and `Microsoft.Extensions.AI.dll`. Build is green; extension-scoped tests pass (2 pre-existing failures in `CompactBehaviorTests`, an area not touched by this task). **Still required on the machine:** close all devenv instances, redeploy the fresh VSIX, purge the stale `k4zrnwab.ltu`/`2mxvgkjx.nf2` folders, restart VS.

## Not implemented (out of scope / needs product decision)
- §11 DPAPI encryption of `ApiKey` — needs design sign-off.
- §14 history compaction — needs a token budget policy.
- §15 real GUIDs + `AssemblyName` rename — breaking to the deployed package identity; do deliberately.
- §16 hide/enable stubbed OpenAI/Anthropic upstreams — product decision.
