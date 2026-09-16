# Code Review — Kaeo LLM Proxy (2026-09-13 pass)

**Started:** 2026-09-13
**Scope:** Full solution — Core, Infrastructure, Services (proxy), WinForms host, runtime modules (Web Search / SSH / Code Vector Store), VS extensions, tests, build configuration.
**Supersedes:** [CODE_REVIEW.md](CODE_REVIEW.md) (2026-07-14 pass) — all 23 of its findings are marked `[RESOLVED]` and are **not** re-reported here. `Plans/20260804-code-review-remaining-issues.md` records that triage.
**Review lenses:** correctness · async/cancellation · disposal/resource lifetime · security (secrets, SSRF, injection, path traversal) · error handling (no silent catches, precise types) · performance/allocations · design/SOLID · localization of user-facing strings · convention consistency.

> This document is updated **incrementally** as each area is read — findings appear before the pass is finished. Out of scope: `ollama-docs`, `API Specs`, `vercel-ai-sdk reference`, `copilot-sdk`, anything under `bin/`/`obj/` (reference material and build output).

## Severity legend

| Tag | Meaning |
|---|---|
| **Critical** | Crash, data loss, or security exposure reachable in normal use |
| **High** | Correctness or resource bug under real load / plausible configuration |
| **Medium** | Robustness, maintainability or UX defect; workaround exists |
| **Low** | Style, naming, dead code, doc drift |

## Review progress

| # | Area | State | Findings |
|---|---|---|---|
| 1 | Solution map | ✅ done | — |
| 2 | Doc skeleton | ✅ done | — |
| 3 | Core | ✅ done | F-04 … F-12 |
| 4 | Infrastructure | ✅ done | F-13 … F-24 |
| 5 | Services: transport (`ProxyServer`) | ✅ done | F-29, F-30 |
| 6 | Services: routing (`HandleCoreAsync`, `PassthroughAsync`) | ✅ done | F-25, F-31, F-02 |
| 7 | Services: normalization / logging / redaction | ✅ done | minor notes |
| 8 | Services: streams (SSE, thinking, tool calls, compaction) | ✅ done | F-01, F-03, F-27, F-28 |
| 9 | Services: translation + MCP host | ✅ done | F-26 |
| 10 | WinForms host | ⚠️ partial (`Program.cs`; UI forms deferred) | F-35, F-36 |
| 11 | Modules (Web Search / SSH / Code Vector Store) | ⚠️ partial (`NetworkSafety`, `SshConnectionManager`) | F-37, F-38, F-39 |
| 12 | VS extensions (+ `MeaiAdapter`) | ❌ not covered | — |
| 13 | Tests + build config | ⚠️ partial (project files; test bodies not read) | F-40, F-41, F-42 |
| 14 | Reconcile + prioritized fix list | ✅ done | see below |

Numbering note: F-01 … F-42 are otherwise contiguous; **F-32 – F-34 were deliberately not opened as findings** — the SSE re-serialisation, the `/api/chat` filter question and the double redaction parse are recorded as "Minor notes (Services)" because each needs product input or a second reading before it is actionable.

## Running totals

| Severity | Open | Findings |
|---|---|---|
| Critical | 0 | — |
| High | 9 | F-01, F-02, F-03, F-04, F-13, F-14, F-25, F-26, F-37 |
| Medium | 16 | F-05 – F-09, F-15 – F-21, F-27, F-28, F-38, F-40 |
| Low | 14 | F-10 – F-12, F-22 – F-24, F-29 – F-31, F-35, F-36, F-39, F-41, F-42 |
| **Total** | **39** | |

The four themes worth acting on as groups rather than as 39 items: **(1)** the tool-call guard is correct in intent but fail-open in three places and route-limited (F-01/02/03/25); **(2)** anything that listens on a port has no mandatory authentication while the app holds credentials and executes remote commands (F-08/13/26/38); **(3)** the SQLite logging path is synchronous, per-event, and never pruned (F-14/15/21); **(4)** configuration that users can see and edit is not always configuration the app reads (F-06/21/42).

---

# Findings

## Services — routing, streams and tool-call guards

Seeded from the investigation of the Copilot `NotificationAction`/`ArgumentNullException` crash; independently re-verified against the current source. The repo already documents this failure mode in its own comments (`OllamaProxyHandler.cs` ~1652: *"a function part the client cannot bind (Copilot UI crash)"*; ~2318: *"the VS Copilot UI crashes on those"*), which is why the three gaps below matter — they are holes in a guard that exists deliberately.

### F-01 — Tool-call guard **fails open** when the declared tool set is unknown · **High** · `Kaeo LLM Proxy Services/OllamaProxyHandler.cs:1812`

```csharp
private static bool IsToolNameAllowed(IReadOnlySet<string>? declaredToolNames, string? name)
	=> declaredToolNames is null                    // ← unknown == allow everything
		|| (!string.IsNullOrEmpty(name) && declaredToolNames.Contains(name));
```

`declaredToolNames` is `null` whenever `ExtractDeclaredToolNames` cannot parse the body (`catch (JsonException)`, ~1799) **or** when `PassthroughAsync` never reaches its assignment (line 1236 is inside `if (isJsonPost)` — a POST whose `Content-Type` is not `application/json`, or a request with no entity body, leaves `passthroughDeclaredTools` at its `null` initializer).

Why it matters: `null` disables the only protection against emitting a function part the client cannot bind, and with a `null` set even `BuildOpenAiToolCallsArray`'s `["name"] = toolCall.Function?.Name ?? string.Empty` (~1759) passes — a **nameless** tool call is precisely what yields a null command in the Copilot UI. The safe default for a proxy that must not hand back unbindable content is fail-closed.

Suggested fix: for recognized Copilot clients treat unknown as restricted (drop + rewrite `finish_reason` to `stop`, which the code already knows how to do), and gate `BuildOpenAiToolCallsArray` on a non-empty name. If legacy permissiveness must stay for non-Copilot callers, make that an explicit branch rather than the `null` case of a shared helper.

### F-02 — Tool-call filtering is bound to one exact route; every other `/v1/*` is copied raw · **High** · `OllamaProxyHandler.cs:1465`, `1502`, `1516`/`1530`, `1555`

```csharp
bool shouldMirrorReasoningContent = IsChatCompletionsPath(req.Url?.AbsolutePath); // "/v1/chat/completions" only
...
if (isServerSentEvents) { if (shouldMirrorReasoningContent) { /* OpenAiSseRewriter */ }
						  else { /* CopyStreamWithSseHeartbeatsAsync — no rewriter */ } }
else { await CopyNonStreamingChatResponseAsync(..., extractToolCalls: shouldMirrorReasoningContent, ...); }
```

`IsChatCompletionsPath` (~1580) is an exact ordinal match. Anything else under `/v1/` is still forwarded by `PassthroughAsync` (routing branch ~1071 matches `path.StartsWith("/v1/")`) but is streamed/copied **without any tool-call guard** — notably `/v1/responses`, whose existence is confirmed by the sibling `/v1/responses/compact` handler (~3399) and by `HandleCompactAsync`. When a mapping points at a hosted provider that implements the Responses surface, `function_call` items pass through untouched and reach VS unfiltered.

Why it matters: the guard is documented as protecting *the client*, but it is only installed on one of several paths that can carry the same content. Also note `ExtractDeclaredToolNames` only understands nested `tools[].function.name`; Responses-style flat `{"type":"function","name":…}` entries would yield an **empty** set — fail-safe for stripping, but it silently breaks tool calling on that shape.

Suggested fix: derive "can this response carry function parts" from the negotiated surface rather than one literal path, and make declared-name extraction understand both tool shapes.

### F-03 — A streamed tool call whose name has not arrived yet is forwarded optimistically · **High** · `OllamaProxyHandler.cs:2339`

```csharp
// Argument fragment of an already-evaluated call — inherit its verdict.
valid = !cs.NativeCallValidity.TryGetValue(callIndex, out bool previous) || previous;
```

The inherit-the-verdict comment assumes a verdict always exists. For `{"index":0,"id":"call_x","type":"function"}` (a nameless first fragment, legal in OpenAI-compatible streams and emitted by several servers) `NativeCallValidity` is empty, so `valid` defaults to `true` and the fragment is written to the client; the *later* chunk that carries the disallowed name is then dropped. Result: VS receives a `tool_calls` delta with an id/type but **no name** — a malformed function part, i.e. the same null-command crash F-01 exists to prevent, and the `HadValidToolCall` bookkeeping at ~2370 then reports a valid call.

Suggested fix: hold each `(choice, callIndex)` delta in the `ChoiceState` buffer until the name is known, then emit-or-drop the whole call; never forward a fragment with no verdict.

---

## Core

Reviewed closely: `Security/SecretProtector.cs`, `Models/AppSettings.cs` (all 1145 lines), `Models/ModelCapabilities.cs`, `Models/ReasoningEffortProfiles.cs`, `Modules/ISecretProvider.cs`, `Modules/ModuleContext.cs`. The remaining Core files (`OllamaTypes.cs`, `RequestLog.cs`, `ExceptionDetail.cs`, `ModuleRegistryEntry.cs`, `McpServerSettings.cs`, `Modules/I*.cs`) were reviewed as DTOs/contracts and produced no defect beyond the notes below.

Positives worth keeping: `ModelMappings`/`InstructionSets`/`Credentials` are `[JsonIgnore]`, so secrets and configuration never round-trip through `settings.jsonc`; `StoredCredential` documents plaintext-in-memory / encrypted-at-rest explicitly; `Normalize()` clamps every numeric before persisting and explains why (a non-positive timeout would build an already-cancelled CTS); `EnsureId`/`TrackMaxId` use `Interlocked`/`Volatile` correctly for the shared ID counter; `SecretProtector` uses AES-GCM with per-call random salt + nonce, a versioned envelope, and zeroes the derived key.

### F-04 — `AppSettings.Save()` writes non-atomically; a crash mid-write silently destroys all configuration · **High** · `Kaeo LLM Proxy Core/Models/AppSettings.cs:876`

```csharp
public void Save()
{
    Normalize();
    try
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, _writeOptions));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex, ...); throw; }
}
```

`File.WriteAllText` truncates and writes in place. A power loss, full disk, or killed process mid-write leaves a truncated `settings.jsonc`. The next `Load()` hits its `catch`, logs a warning and returns **defaults** (~line 868), and because settings are saved immediately on every edit, the first subsequent change overwrites the corrupted file entirely. Result: all model mappings, listen port, redaction and privacy flags are lost, with a single `Log.Warning` as evidence.

Suggested fix: serialize to `settings.jsonc.tmp`, then `File.Replace(tmp, path, backupPath)` (or flush + `File.Move(..., overwrite: true)`). Also move an unparseable file to `settings.jsonc.bak-<timestamp>` in `Load()` before falling back, so the last known configuration stays recoverable.

### F-05 — `CredentialMaterial` is a positional `record`, so its synthesized `ToString()` prints API keys and SSH private keys · **Medium** · `Kaeo LLM Proxy Core/Modules/ISecretProvider.cs:12`

```csharp
public sealed record CredentialMaterial(
    string Name, string? Username, string? Secret, string? PrivateKey, string? Certificate);
```

Positional records synthesize `ToString()`/`PrintMembers` over **every** member. This type is `public` — it is the plugin-facing value modules receive from `ISecretProvider.ResolveCredential` — so a module that writes `Log.Information("cred {Credential}", material)` or interpolates `$"{material}"` dumps a bearer token, private key or certificate into the Serilog app log. No such sink exists today (`ModuleSecretProvider.cs:30,36` and `Module SSH/Mcp/SshTools.cs:368` read individual properties only), hence Medium rather than High — but the API shape invites the leak and the host cannot prevent it once a module holds the value.

Suggested fix: override `ToString()` to emit only `Name` plus which fields are populated, or make it a sealed class with explicit getters. Note the residual non-zeroable exposure of `string` secret material in the interface remarks.

### F-06 — Four behaviour flags are unreachable: `[JsonIgnore]`, absent from the runtime round-trip, no UI writer · **Medium** · `Models/AppSettings.cs:798`, `807`, `825`, `919`

`UseIrTranslation`, `EnableAutoCompaction`, `EnableCopilotNativeCompaction` and `EnableManualCompactionEndpoint` are `[JsonIgnore]` on `AppSettings`, none appears in `CreateRuntimeSettings()`/`ApplyRuntimeSettings()` (that round-trip covers only `AutoStartProxy … EnableApiExplorer`), and usage search shows each is **read** only by `OllamaProxyHandler` (1063, 1243, 4235, 4335, 4341) and written only by tests. Consequences:

1. `UseIrTranslation` is permanently `false`, so the whole Microsoft.Extensions.AI route (`Translation/OllamaRequestTranslation.cs`, `OpenAiInbound`, `OpenAiOutbound`) ships as dead code exercised only by `OllamaIrParityTests`/`MeaiAdapterTests` — two parallel translation implementations, one of which no user can enable or validate.
2. `EnableManualCompactionEndpoint` is permanently `false`, so `/v1/chat/completions/compact` always answers 404 and `HandleManualCompactAsync` (~3590) is unreachable.
3. `EnableAutoCompaction` is permanently `true`, so a **lossy** transformation (history summarization by a second model) always runs for non-Copilot clients with no off-switch anywhere.

Suggested fix: add the flags to the `RuntimeSettings` round-trip and surface them in the dashboard (auto-compaction at minimum needs a visible toggle plus a status line), or delete the flags and hard-code the intended behaviour so the IR route is either live or removed.

### F-07 — `Normalize()` does not enforce name uniqueness, so a duplicate mapping is silently shadowed · **Medium** · `Models/AppSettings.cs:893` (with `ResolveModelName`/`FindModelMapping` ~968/~990)

`FindModelMapping` and `ResolveModelName` return the **first** enabled match, and `FindModelMapping` matches on `ProxyName` *or* upstream `ModelName`. Two mappings sharing a `ProxyName` (trivially created by duplicating a grid row; nothing in `Normalize()` rejects it) make the second unreachable while it still holds an upstream URL/credential; two mappings sharing an upstream `ModelName` cross-resolve, so `ResolveEffectiveModel`/compact-redirect routing can select the wrong row and send traffic to the wrong server or summarization model. The same first-match pattern applies to `FindCredential` and `FindInstructionSet`.

Suggested fix: detect duplicates in `Normalize()` and log which mapping is shadowed (or block the commit in the UI with a validation message); prefer resolving by the stable `Id` for internal routing.

### F-08 — No authentication on the listener; a non-localhost bind hands the configured upstream credential to anyone who can reach the port · **Medium** · `Models/AppSettings.cs:650`, `668` + `Services/OllamaProxyHandler.cs:1190`, `821`

`ListenAddress` is user-configurable (`localhost`, `0.0.0.0` or a specific IP — documented at line 645), the proxy has no access token of its own, and passthrough copies the client's `Authorization` header only to let `ApplyApiKey` **override** it with the mapping's stored credential. With a wildcard bind, any LAN peer can consume the upstream account (quota/billing) using the operator's key, enumerate `/api/tags`, and drive log growth. `EnableCors` (`Access-Control-Allow-Origin: *`, line 668) widens this to browser-driven abuse; even with CORS off, preflight-free request shapes and DNS rebinding against a loopback bind remain possible because the `Host` header is not validated. Defaults are safe (loopback, CORS off), so this is config-gated — but the mitigation is absent, not merely undocumented.

Suggested fix: require a proxy access token when the effective bind address is not loopback (checked once in `HandleCoreAsync`), or at minimum show a first-run confirmation plus a persistent dashboard warning; validate inbound `Host` against the expected address/port. The existing `EnableCors` comment is the right tone — `ListenAddress` deserves the same.

### F-09 — `SecretProtector.TryDecrypt` cannot fulfil its own contract · **Medium** · `Kaeo LLM Proxy Core/Security/SecretProtector.cs:113`

```csharp
/// <returns>false when the passphrase is wrong or the data is corrupt.</returns>
public static bool TryDecrypt(string envelope, string passphrase, out string? plaintext)
{
    try { plaintext = Decrypt(envelope, passphrase); return true; }
    catch (CryptographicException) { plaintext = null; return false; }
}
```

Corrupt data is not only a `CryptographicException`: `Convert.FromBase64String(envelope[EnvelopePrefix.Length..])` (~84) throws `FormatException` on a malformed payload, and a value carrying the prefix but otherwise invalid reaches `ArgumentException.ThrowIfNullOrEmpty` inside `Decrypt`. Both escape the Try-pattern, which is precisely where callers handle failure — so a damaged credential row can abort startup/decryption instead of degrading. Related hygiene in the same file: `Decrypt` never zeroes the `plaintext` buffer after `Encoding.UTF8.GetString` (~100), and `DeriveKey` allocates `Encoding.UTF8.GetBytes(passphrase)` that is never cleared (~123), leaving the passphrase copy alive past the call. `Iterations = 100_000` also sits below current PBKDF2-SHA256 guidance; because the envelope is versioned (`kaeo-enc:v1:`), a `v2` with a stronger count and re-encryption on next save is backward compatible.

Suggested fix: catch `CryptographicException or FormatException or ArgumentException`; zero `plaintext` in the `finally`; prefer a `ReadOnlySpan<char>` passphrase overload (`Rfc2898DeriveBytes.Pbkdf2` accepts one) to avoid the copy; introduce `v2` with the higher iteration count.

### F-10 — `Kaeo LLM Proxy Core/AppSettings.cs` is a 0-byte file still compiled into the project · **Low**

Confirmed by file-size listing (`0 KB`) while the real model lives at `Models/AppSettings.cs`. Harmless under SDK globs but it shadows the type name in Solution Explorer and in search results. Delete it.

### F-11 — `ModelCapabilities.Normalize` enumerates its input ten times · **Low** · `Models/ModelCapabilities.cs:47`

`ContainsToken(tokens, t)` re-enumerates the caller's sequence for each of the nine known tokens plus a final pass: O(K·N) and, worse, silently empty results for a one-shot `IEnumerable<string>` (`yield`, `File.ReadLines`, a LINQ projection). Materialize once (`if (tokens is not IReadOnlyList<string> list) list = tokens.ToArray();`) and test membership against a case-insensitive `HashSet`.

### F-12 — `ModelMapping.Clone()` is hand-maintained with no completeness guard · **Low** · `Models/AppSettings.cs:518`

`Clone()` enumerates all 25 properties literally, and `MainForm.TryCommitMappings` clones every row on each commit (asserted by `ModelMappingCloneTests.ContextSummarizeReferenceSurvivesCommitStyleCloning`), so a property added later and forgotten here is silently dropped on every save — it reproduces as "this setting won't stick". Existing tests cover only ID semantics. Fix: add a reflection test asserting every settable instance property is copied from a fully populated source.

## Infrastructure

Reviewed closely: `AppDatabase.cs` (2389 lines — ctor/connection model, `Insert`, `DeleteOlderThan`, `PrepareDatabaseFile`, `OpenConnection`, all four `ExecuteModule*` entry points, `GetSystemLogs`, `Dispose`), `Modules/ModuleHost.cs` (425 lines), `Modules/ModuleAssemblyLoadContext.cs`, `SystemLogDbSink.cs`, `AppLogger.cs`, `PooledCharBuffer.cs`, `UpstreamUriHelper.cs`, `Modules/ModuleDatabaseGateway.cs`, `Modules/ModuleSecretProvider.cs`. `HelpPages.cs` (static HTML), `LoadedModule.cs`, `LateBoundActivityLog.cs`, `Mcp/*` reviewed as plumbing.

Positives: every SQL statement in the host's own data access is parameterized (the dynamic `IN (...)` in `DeleteOlderThan` builds *parameter placeholders*, not values — correct); the table name comes only from `RequestTable(LogSource)` over an enum; `Insert` uses a transaction; `PrepareDatabaseFile` renames a misidentified file to a timestamped `.bak` instead of deleting it; WAL is enabled conditionally (prior fix) and the comment explains why shared-cache was deliberately rejected.

### F-13 — Module plug-in surface grants arbitrary SQL over the host database and every plaintext credential · **High** · `AppDatabase.cs:2262`‑`2322`, `Modules/ModuleDatabaseGateway.cs:14`, `Modules/ModuleSecretProvider.cs:13`, `Modules/ModuleHost.cs:117`

Modules receive `IModuleDatabase` whose methods pass the caller's SQL text straight to `ExecuteModuleNonQuery/Scalar/Query/SchemaScript` on the **same** connection string the host uses, with the same write privileges — so a module can `SELECT * FROM credentials`, read `requests.request_body`/`response_body` (other users' prompts, which may contain source code), or `DROP` host tables through `ExecuteSchemaScript`. The same `ModuleContext` hands out an `ISecretProvider` that exposes `ListCredentialNames()` plus `ResolveCredential(name)` for **any** name, i.e. every API key, SSH private key and certificate in the store. `ModuleHost.Import` validates only that exactly one `IKaeoModule` exists — there is no signature/publisher/hash check on the `assemblyPath` the user browsed to, and the registry stores whatever path is given.

Each piece is individually defensible ("trusted local module"), but together they mean *any* DLL dropped in `Modules/` runs with the host's full data and credential access, and with the SSH module present it can then push commands to remote hosts. Suggested fix: give each module its own database file (or at minimum a read-only, table-prefixed connection — SQLite `SQLITE_LIMIT/authorizer` or a separate file per `ModuleId`); make credential access per-module and explicitly approved (`IKaeoModule` declares the credential names it needs; `ModuleSecretProvider` throws for anything else); record a SHA-256 of the imported assembly in `module_registry` and verify it on load.

### F-14 — Every log event opens a SQLite connection, runs three DDL statements, and holds a lock while doing synchronous I/O · **High** · `SystemLogDbSink.cs:79`‑`97`, `60`‑`73`

```csharp
private void TryEmitToDb(...)
{
    using SqliteConnection connection = new(_connectionString);
    connection.Open();
    EnsureTableExists(connection);          // CREATE TABLE IF NOT EXISTS x3 + CREATE INDEX x2
    ... command.ExecuteNonQuery();
}
```

`Emit` wraps the write in `lock (_lock)`, so **all** logging in the process serialises through one mutex and through a fresh connection per event. `Log.Information` is called on the proxy's request path, and Serilog's `WriteTo.Sink` is synchronous — so a request thread pays connection-open + DDL + INSERT latency, and blocks behind every other thread that is logging. If the database is momentarily locked (see F-20), the I/O inside the lock can stall the request pipeline outright. `EnsureTableExists` in particular is schema churn that belongs in the constructor.

Suggested fix: open one long-lived connection at sink construction (or reuse the host's), create the table once, and make persistence asynchronous — a bounded `BlockingCollection`/`Channel` drained by a single writer thread, or `WriteTo.PeriodicBatching`, with an explicit drop counter so back-pressure never lands on request threads.

### F-15 — `system_logs` is never pruned, so the database grows without bound · **Medium** · `AppDatabase.cs:847` vs `2329`/`2373`

`LogRetentionHours` is implemented by `DeleteOlderThan`, which deletes only from `RequestTable(source)` and its `exceptions` rows. The `system_logs` table that `SystemLogDbSink` inserts into on every event is only ever emptied manually via `ClearSystemLogs()`. Combined with F-14 (one row per log event, including full exception text), a long-running proxy with `MinimumLevel=Debug`/`DebugMode` bloats the shared database file; and because SQLite does not reclaim space on `DELETE`, even `ClearLogs`/`ClearSystemLogs` leave the file large (no `VACUUM`/`incremental_vacuum` anywhere).

Suggested fix: include `system_logs` in `DeleteOlderThan` (or add a `SystemLogRetentionHours`), and run `PRAGMA incremental_vacuum` after large deletes — plus add the `(timestamp_utc)`-based retention index the sink's own `EnsureTableExists` already creates.

### F-16 — Unloading one module disables dependency resolution for the modules that share its folder · **Medium** · `Modules/ModuleAssemblyLoadContext.cs:46`, `68`, `75`

`_moduleDirectories` is a `HashSet<string>` of *directories*, added in the constructor and removed in the `Unloading` handler — but all modules are deployed into a single folder: the host project sets `<CopyModulesToHost>true</CopyModulesToHost>` and `ModuleBuild.targets` copies every module's output (DLL + dependencies) to `$(OutputPath)Modules/`. Disabling or removing one module therefore removes `Modules/` from the set, and `OnDefaultContextResolving` can no longer find dependencies for the modules still loaded — surfacing later as confusing `FileNotFoundException`/`TypeLoadException` from an unrelated module.

Suggested fix: reference-count each directory (or key the set by `(directory, owning context)`) so an entry is removed only when its last context unloads; simpler and more robust: give each module its own subfolder, which also fixes the version-hijack in F-17.

### F-17 — The default-context fallback loads module assemblies into the non-collectible default context · **Medium** · `ModuleAssemblyLoadContext.cs:107`‑`120`

`OnDefaultContextResolving` is registered on `AssemblyLoadContext.Default` and calls `context.LoadFromAssemblyPath(candidate)`, i.e. it binds a **module's** private dependency into the host's default, non-collectible context. Consequences: the assembly can never be unloaded (it outlives `RemoveAsync`), and because all modules register into the same set, the first module that happens to resolve a given simple name wins that binding for every other module and for the host — a silent cross-module version hijack. `_resolvingHandlerRegistered` is also set once and the handler is never detached, so the hook survives after all modules are gone.

Suggested fix: keep module dependencies inside their own collectible context (that is what `Load`'s probing already does) and reserve the default-context hook for a narrow, logged list of known contract assemblies; unregister the handler when the last module unloads; log at `Warning` whenever it fires so the coupling is visible.

### F-18 — Type unification ignores assembly versions, so a module silently binds the host's older copy · **Medium** · `ModuleAssemblyLoadContext.cs:124`‑`140`

`Load` step 0 returns `null` (defer to default context) whenever any default-context assembly's **simple name** matches, and step 1 defers whenever the host's `deps.json` can resolve the name. A module built against a newer version of a shared dependency (the host deliberately force-upgrades `MessagePack` to 3.1.8 for the CVE in the csproj comment) therefore gets the host's copy and fails later with `MissingMethodException`/`TypeLoadException` far from the cause.

Suggested fix: compare `assemblyName.Version` against the host candidate and, when the module asks for something newer than the host provides, load from the module's own directory in a separate context *and* log a clear warning naming both versions. This is the same class of bug as F-17 and is best fixed by per-module folders.

### F-19 — `ModuleHost` mutates a plain `List` from UI operations that request threads enumerate; enabling can double-load a module · **Medium** · `Modules/ModuleHost.cs:22`, `59`‑`79`, `199`‑`214`, `313`‑`317`

`_loadedModules` is an unsynchronized `List<LoadedModule>`: `GetMcpToolTargets` iterates it on MCP server threads while `Import`/`SetEnabledAsync`/`RemoveAsync`/`UnloadAsync` add and remove on the UI thread → `InvalidOperationException: Collection was modified` or a torn read. `SetEnabledAsync` additionally has a check-then-act gap across an `await`: `if (!_loadedModules.Any(m => m.Entry.Id == entry.Id)) { LoadedModule loaded = LoadModule(entry); _loadedModules.Add(loaded); }` — two overlapping enable clicks (the `guard-buttons-spam-clicks` plan exists precisely because clicks land twice) can both pass the check, so the module is loaded and `Initialize`d twice, one context is orphaned and never unloaded. `UnloadAsync` also calls `LoadContext.Unload()` while `McpSessionInfo`/tool target instances obtained earlier may still be executing.

Suggested fix: guard the list with a `Lock` and expose a snapshot copy to `GetMcpToolTargets`; make enable/disable idempotent by holding a per-entry `SemaphoreSlim` (or a `_loading` `HashSet<int>`) rather than testing list membership; stop and drain MCP sessions for that module before `Unload()`, and log if the context fails to collect.

### F-20 — No `busy_timeout` while multi-instance operation and a second writer are supported configurations · **Medium** · `AppDatabase.cs:1789`

```csharp
private SqliteConnection OpenConnection()
{
    SqliteConnection connection = new(_connectionString);
    connection.Open();
    return connection;
}
```

`AppDatabase` serialises its own writers with `_lock`, but the design deliberately allows other writers to hit the same file: `AllowMultipleInstances` lets a second process share it (`PrepareDatabaseFile` has an explicit "locked by another process" path), and `SystemLogDbSink` writes through its **own** connection per F-14. Without `PRAGMA busy_timeout`/`SqliteConnection.BusyTimeout`, a concurrent write fails immediately with `SQLITE_BUSY` instead of waiting, which surfaces as dropped request logs or a failed `SaveRuntimeSettings`.

Suggested fix: set `connection.BusyTimeout = TimeSpan.FromSeconds(5)` (or `PRAGMA busy_timeout=5000;`) immediately after `Open()`, and wrap `Insert` in a small retry-with-backoff for `SqliteException` whose `SqliteErrorCode` is `SQLITE_BUSY`, so telemetry loss is graceful rather than silent.

### F-21 — Two logging settings are wired to UI controls but nothing reads them · **Medium** · `Core/Models/AppSettings.cs:741`‑`748`, `MainForm.cs:1497`, `1618`

`LoggingSettings.AppLogFileSizeLimitMb` and `AppLogRetainedFileCount` are documented as "Maximum size in MB of a single app-log file before rolling" / "Number of rolled app-log files to retain", are surfaced in the dashboard and written back, yet usage search shows the only references are the `settings.jsonc` template and those two UI sites — `AppLogger.Initialize` configures exactly two sinks (in-memory + DB) and no rolling file sink, so nothing rolls or retains. The previous pass also recorded `RequestLogFileSizeLimitMb` as unused. Users can change a limit that has no effect while the real growth problem (F-15) has no control at all.

Suggested fix: either add `WriteTo.File(..., rollingInterval: ..., rollOnFileSizeLimit: true, retainedFileCountLimit: ...)` so the knobs work, or remove the properties plus their UI and replace them with a single, honest "system log retention (hours)" setting that `DeleteOlderThan` honours.

### F-22 — `PooledCharBuffer` returns pooled arrays without clearing and assumes single-threaded use · **Low** · `PooledCharBuffer.cs:66`, `75`

`ArrayPool<char>.Shared.Return(_buffer)` in `Grow`/`Dispose` omits `clearArray: true`, so LLM request/response text handed to the shared pool is readable by the next renter in the process — at odds with the app's redaction-by-default posture. `_disposed` is also a plain `bool`, so `Dispose` racing `Append` can write into an array another consumer already owns (pool corruption rather than a clean `ObjectDisposedException`). Fix: return with `clearArray: true` (or document/`Lock` the single-thread contract).

### F-23 — Silent catches and a stale comment in the logging/host plumbing · **Low** · `SystemLogDbSink.cs:107`, `155`; `ModuleAssemblyLoadContext.cs:138`; `AppDatabase.cs:1722`

`catch` blocks with no logging in `TryEmitToDb`/`TryTestDbConnection` mean the logging subsystem can fail permanently with no signal (only the observable `IsUsingDatabase` flag), violating the repo rule against swallowing errors; `ModuleAssemblyLoadContext.Load` step 0 has a bare `catch` too. `PrepareDatabaseFile`'s comment still says "the shared-cache connection below will fail with a clearer error", but the constructor explicitly rejected shared-cache mode — the reader is sent looking for code that does not exist. Fix: `SelfLog.WriteLine` (or a one-shot `Log.Debug`) inside those catches, and reword the comment.

### F-24 — `GetSystemLogs` throws for the whole grid on one malformed timestamp row · **Low** · `AppDatabase.cs:2361`

`DateTime.Parse(reader.GetString(0), InvariantRuntime, RoundtripKind)` is unguarded, so a row written by an older build (or truncated by an interrupted write) makes the System Logs tab fail entirely instead of showing that entry with a fallback date. Use `DateTime.TryParse` and fall back to `default`/filetime ordering (`ORDER BY id DESC` already gives the correct order).

## Services — transport, routing, streams, MCP

Read closely: `ProxyServer.cs` (all 325 lines), `Mcp/McpServerHost.cs` (transport, session lifecycle, auth, health), `OllamaProxyHandler.cs` — `HandleAsync`/`HandleCoreAsync` routing, `PassthroughAsync` (request and response halves), `ResolveUpstream`/`SendUpstreamAsync`/`ApplyApiKey`/`BuildHttpClient`, `ReadBodyAsync`, `CopyNonStreamingChatResponseAsync`/`TransformNonStreamingChatBody`, `BuildOpenAiToolCallsArray`/`ExtractDeclaredToolNames`/`IsToolNameAllowed`, `OpenAiSseRewriter` + `ChoiceState`, `RedactRequestBodyForLog`/`RedactSensitiveJsonFields`; `Translation/OpenAiInbound.cs` (full).

Positives: `BuildHttpClient` deliberately does **not** enable `AutomaticDecompression`, so upstream gzip is copied through with its header intact (a classic proxy corruption bug avoided) and the per-request timeout is a linked CTS rather than `HttpClient.Timeout`; `ProxyServer` observes the pending `GetContextAsync` fault before exiting, guards slot release against `ObjectDisposedException`/`SemaphoreFullException`, tunes `TimeoutManager` per-listener, and turns ERROR_ACCESS_DENIED into an actionable `netsh urlacl` command; `McpServerHost` compares bearer tokens with `CryptographicOperations.FixedTimeEquals`, generates 128-bit `RandomNumberGenerator` session ids, resolves the auth secret live, and tears sessions down in the SDK's documented order; `RedactSensitiveJsonFields` preserves the body byte-for-byte outside redacted values (so logs show what the client really sent) and defaults to redacting when a mapping is missing; the SSE rewriter flushes `finish_reason` to `stop` when every call was filtered so the client never waits for tool results that will not arrive.

### F-25 — Inbound `Content-Encoding` is never decoded: a compressed body silently disables request normalization **and** the tool-call guard · **High** · `OllamaProxyHandler.cs:5225` (`ReadBodyAsync`)

`ReadBodyAsync` reads `req.InputStream` raw and hands it to `req.ContentEncoding.GetString(...)`; `HttpListener` does not decompress request entities. So for a client that sends `Content-Encoding: gzip`, the "JSON" the proxy operates on is mojibake, and three independent consequences follow: `NormalizeRequestBody` returns the text unchanged (its own contract — model rewrite, sampling priorities, reasoning-effort injection all silently skipped); `IsStreamingJsonBody`'s regex fails, so SSE headers are never pre-committed for a streaming call; and `ExtractDeclaredToolNames` hits its `catch (JsonException)` and returns **`null`**, which per F-01 switches the tool-call guard off — the exact fail-open that produces the Copilot crash. The upstream then receives re-encoded mojibake.

Suggested fix: when `Content-Encoding` is `gzip`/`deflate`, decompress while enforcing `MaxRequestBodyBytes` on the *decoded* size; otherwise reject with `415 Unsupported Media Type` so the behaviour is explicit instead of silently degraded. Also check whether `ShouldSkipRequestHeader` (~1150) drops the client's `Content-Encoding` on the way out — if it does not, the rewritten body is still labelled gzip.

### F-26 — The MCP endpoint can be reachable from the network with authentication disabled · **High** · `Mcp/McpServerHost.cs:733` (`IsAuthorized`), `104` (`StartAsync`), `Core/Models/McpServerSettings.cs:24`

`IsAuthorized` returns `true` whenever `AuthCredentialName` is empty (documented as "Null/empty disables authentication"), and `StartAsync` maps `""`/`*`/`0.0.0.0`/`+` to a `+` prefix — a wildcard bind. Defaults are safe (`ListenAddress = "localhost"`), but nothing prevents or flags the combination where a user sets a non-loopback address and never configures a token. Unlike the proxy listener (F-08), what sits behind this port is `McpToolModule`-provided tools: the SSH module's remote command execution, the web-search fetcher, and the code index — i.e. unauthenticated LAN access to running commands on hosts the user's credentials unlock. `/health` is served before authorization and discloses uptime plus `activeSessions`.

Suggested fix: refuse to bind a non-loopback address unless `AuthCredentialName` resolves to a non-empty secret (clear message, one-click fix), or force loopback and say so; move `/health` behind the same check.

### F-27 — An unclosed `<tool_call>` in `delta.content` swallows the rest of the answer · **Medium** · `OllamaProxyHandler.cs:2410` (`ChoiceState.IngestToken`), `2456` (`EmitToolCallFromBuffer`)

`IngestToken` sets `_inToolCall` on seeing `<tool_call>` and then diverts **all** subsequent content into `_toolBuffer` until a `</tool_call>` appears; `EmitToolCallFromBuffer` bails out (`if (!m.Success) return;`) when the block never parses, and the buffer is only cleared on the closing-tag path. A model that merely quotes the tag in prose — or emits it and streams on — therefore loses the remainder of the reply and grows an unbounded buffer for the life of the stream. The non-streaming path is safe by contrast because `XmlToolCallRegex` demands a closed block.

Suggested fix: bound `_toolBuffer` (discard + flush as visible text when exceeded or at end-of-stream), and only enter `_inToolCall` for a plausible call opening (e.g. require the `<function=` attribute within the lookahead) so prose mentions pass through.

### F-28 — Reading a request body costs up to three full copies, multiplied by concurrency · **Medium** · `OllamaProxyHandler.cs:5225`

`ReadBodyAsync` grows a `MemoryStream`, calls `ToArray()` (copy 2), then `GetString(...)` (copy 3) on a body allowed up to `MaxRequestBodyBytes` (10 MB by default), with `MaxConcurrentRequests` at 64 — hundreds of MB of short-lived LOH pressure precisely when Copilot sends a long conversation. Fix: rent one buffer (`ArrayPool<byte>.Shared`, sized to the limit), and decode once with `Encoding.GetString(span)`; or stream the body straight into `JsonDocument.ParseAsync` and re-serialize only when a rewrite is actually needed.

### F-29 — `Dispose` blocks the UI thread for up to five seconds at shutdown · **Low** · `ProxyServer.cs:299`

`listenTask.Wait(TimeSpan.FromSeconds(5))` is sync-over-async on whatever thread disposes the server — `TrayApplicationContext.Dispose`, i.e. the UI thread — so an in-flight request can freeze the tray/UI. `IsRunning`, `ListenAddress`, `ListenPort` and `_disposed` are also plain auto-properties/fields written by the accept loop and read by the dashboard, so status text can lag or tear. Fix: `StopAsync` already awaits the loop correctly — have `Dispose` call it and wait on a `Task.WhenAny(..., Task.Delay(5s))`, or make shutdown fully async; mark the status fields `volatile`/`Volatile.Read`.

### F-30 — The overload rejection can be cancelled before it ever writes · **Low** · `ProxyServer.cs:203`

`_ = Task.Run(() => RejectOverloadedAsync(context), ct)` binds the 503 write to the server's lifetime token; during a stop, the queued task may be cancelled before running, leaving the client's connection unanswered until http.sys times it out. `RejectOverloadedAsync` already swallows I/O failures, so pass `CancellationToken.None` and let it try to flush.

### F-31 — Multi-valued upstream response headers are collapsed · **Low** · `OllamaProxyHandler.cs:1405`

`resp.Headers[header.Key] = string.Join(",", header.Value)` merges repeated headers, which corrupts `Set-Cookie` (comma is a legal cookie attribute separator) and `WWW-Authenticate`. Use `AppendHeader(key, value)` per value.

### Minor notes (Services)

- `HandleCoreAsync` (~1130) deliberately returns a generic `"Internal proxy error."` so internals never leak, but `PassthroughAsync` (~1427) forwards the upstream error body to the client verbatim — one consistent policy would be better (log raw, forward a summarised body, keeping the 413 rewrite).
- `IsStreamingJsonBody` (~1831) regex-scans the whole body, so a nested `"stream": true` inside tool arguments is misread; a `Utf8JsonReader` scan of the top-level property is both cheaper and correct.
- `RedactRequestBodyForLog` is `static` and takes `settings`, while `RedactResponseBodyForLog` is an instance method reading `_settings`; and `RedactSensitiveJsonFields` re-parses the body to validate it although every caller has already parsed it.
- `OpenAiSseRewriter` re-serialises every SSE frame (`root.ToJsonString`) even when `ThinkingMode.LeaveInline` and no tool calls were present — the common case could be a pass-through of the original line.
- `/api/chat` (Ollama-native) synthesises tool calls from inline XML too; only the OpenAI passthrough was verified to apply the declared-name filter — worth confirming the Ollama route has the same guard.

## WinForms host

Reviewed: `Program.cs` (elevation/relaunch, passphrase resolution, `TryDecryptAllSecrets`, unhandled-exception reporting). `MainForm.cs` + dialogs + `TrayApplicationContext.cs` are **not** covered in this pass — see "Coverage gaps".

Positives: `TryDecryptAllSecrets` is correctly **all-or-nothing** — a first verification pass decrypts nothing and bails on the first failure, so credentials can never end up half-plaintext — and it covers all three secret fields; `TryRelaunchElevated` sets `WorkingDirectory` to the base directory on purpose (settings/`Data` are CWD-relative) and treats a declined UAC prompt as "continue unelevated" instead of failing.

### F-35 — A wrong stored passphrase is erased and persisted before the user gets a chance to fix it · **Low** · `Program.cs:163`‑`167`

```csharp
// Stored passphrase is wrong; remove it so it is not reused on next launch.
settings.SecurityPassphrase = null;
settings.Save();
```

This runs *before* the prompt loop, so a transient mistake (typo in the setting, a settings file restored from an older backup, a passphrase that was edited elsewhere) destroys the stored value immediately — and through the non-atomic `Save()` of F-04. The user then has to retype it, and if they also decline the prompt, the credential material stays encrypted for the session while the file no longer records what was tried. Suggested fix: keep the stored value until a *new* passphrase verifies successfully, and note that this whole flow writes `settings.jsonc` very early in startup.

### F-36 — Startup error paths swallow exceptions and hard-code UI text · **Low** · `Program.cs:257`, `274`

`GetApplicationIcon` and the `MessageBox` inside `ShowUnhandledException` both `catch { }` with no diagnostic. The fallback behaviour is right for a last-resort handler, but the repo's own rule is "no silent catches": route the swallowed failure through `Debug.WriteLine`/`SelfLog`. `ShowUnhandledException` also composes its dialog text from `ex.GetType().FullName`, `ex.Message` and `ex.StackTrace` as literal strings, and the message strings throughout `Program.cs` (and `ResolvePassphrase`'s prompts) are hard-coded rather than in resources, which is the localisation rule the rest of the app mostly follows.

## Modules

Reviewed: `Module Web Search/Core/NetworkSafety.cs`, `Module SSH/Core/SshConnectionManager.cs`. Everything else (search providers, `DomainPolicyService`, `HtmlTextExtractor`, `WebSearchService`/`Repository`, the SSH tools/repository/activity logger, the whole Code Vector Store module) is **not** covered in this pass — see "Coverage gaps".

`NetworkSafety` is a genuinely careful guard: scheme allow-list, `IPAddress.TryParse` for literals, resolution of **every** returned A/AAAA record (a name resolving to both public and private is treated as private), IPv4-mapped-IPv6 normalisation, and correct handling of `0/8`, `10/8`, `172.16/12`, `192.168/16`, `169.254/16` and CGNAT `100.64/10`.

### F-37 — The SSRF guard validates DNS once and cannot see redirects or the address actually connected to · **High** · `Module Web Search/Core/NetworkSafety.cs:24`

`ValidateAsync(uri, allowLocalNetworks, ct)` answers "is *this name's current DNS answer* public?" and then the fetch happens separately, so:

1. **Redirects bypass it.** A public URL that the model fetches can `302` to `http://169.254.169.254/latest/meta-data/` (cloud credentials) or `http://localhost:11434/api/chat` (the proxy's own listener, whose default is loopback — so this is *not* covered by the guard's assumptions). Unless the search providers set `AllowAutoRedirect = false` and re-validate each hop, the check is decorative on the second request.
2. **Rebinding.** The resolver's answer and the answer used by `HttpClient` come from separate lookups, so a short-TTL record can flip between them.
3. **Range gaps.** `IsPrivateOrLoopback` omits multicast `224.0.0.0/4` (incl. mDNS `224.0.0.251`), benchmarking `198.18.0.0/15`, TEST-NET/documentation ranges, and `240.0.0.0/4`; for IPv6 it relies on `IsIPv6SiteLocal`, which has been deprecated/always-false for years, so the IPv6 verdict is effectively loopback + link-local + ULA only.

Suggested fix: resolve once, then connect to the validated address explicitly — `SocketsHttpHandler.ConnectCallback` (pin the endpoint and send the original `Host`/SNI) — plus re-validation on every redirect hop, `AllowAutoRedirect = false` with a bounded manual hop loop, and the missing ranges. This belongs in `Core` so the SSH/Code-Vector modules and the MCP `fetch`-shaped tools share one implementation.

### F-38 — An SSH connection opened by one MCP session is usable by every other session · **Medium** · `Module SSH/Core/SshConnectionManager.cs:76`‑`96`

`ConnectAsync` reuses a live connection by `request.Key` regardless of who opened it, and `ManagedConnection` records the opener only for display. Two MCP clients (or an LLM agent in one VS window and another in a second) therefore share shell state, working directory and — because the SSH key/certificate is resolved from the **central credential store**, not per session — the identity of the first caller. The fast path (`_connections.TryGetValue` before `_connectLock`) also returns a connection that a concurrent `Disconnect`/idle sweep may be closing.

Given F-26 (MCP auth optional) and F-13 (modules see all credentials), this is the third leg of the same problem: tool state and identity are global, not session-scoped. Suggested fix: key the pool by `(session id, key)` — or at minimum require the caller to name a credential the session was authorised for — and re-validate `Client.IsConnected` after taking the lock on the reuse path.

### F-39 — `Start()` can leak a duplicate idle-sweep timer · **Low** · `Module SSH/Core/SshConnectionManager.cs:52`

```csharp
if (_idleSweepTimer is not null) return;
_idleSweepTimer = new Timer(SweepIdleConnections, null, SweepInterval, SweepInterval);
```

`Start()` is called from `ConnectAsync` (any MCP thread) and from the host's runnable start, but `StopAsync` uses `Interlocked.Exchange` while `Start()` uses a plain null check and assignment. Two concurrent first-connections can both pass the guard; one timer is then orphaned — never disposed, still sweeping and resurrecting `IsRunning` semantics after `StopAsync`. Fix: `Interlocked.CompareExchange(ref _idleSweepTimer, new Timer(...), null)` and dispose the loser.

## VS extensions

**Not reviewed in this pass.** `Kaeo VS Extension` (26 code files: `AgentRuntime`, `ChatEngine`, `McpServerManager`, `McpToolAIFunction`, `BuiltInVsTools`, `OllamaApiClient`/`OllamaChatClient`/`UpstreamClients`, `ExtensionSettingsStore`, `InstructionFileLoader`, `BuiltinAgents`, tool windows, settings VMs, `Polyfills.cs`) and `Kaeo LLM Proxy VS Extension.Core/MeaiAdapter.cs` still need their own pass. This project is also the one where the constraints differ most — `net48`/VS-shell bindings, no modern C#, and the VS threading analyzers are active, so it deserves a dedicated review rather than being swept in here.

Two things worth checking when that pass happens, based only on what is visible from the outside: (a) whether settings are persisted through a store that the main app also reads, since `Kaeo LLM Proxy.csproj` references `Microsoft.VisualStudio.Utilities` while `Kaeo VS Extension` targets `net48` — a shared `KaeoCore` surface across two runtimes is easy to desynchronise; (b) whether `bin/` and `obj/` output under `Kaeo VS Extension` is tracked in git (the file listing surfaced build output, including a committed-looking `Assets\Msvc.Info.vsix` at the root, which suggests vendored binaries do get committed).

## Tests and build configuration

Reviewed: the host `.csproj` (in context), test inventory (15 files). No new defects are claimed for the test *bodies* — see coverage gaps.

### F-42 — Duplicated and redundant items in the host project file · **Low** · `Kaeo LLM Proxy.csproj`

The same item is declared twice — `<None Include="Kaeo LLM Proxy VS Extension.Core\Kaeo LLM Proxy VS Extension.Core.csproj" />` appears in two separate `ItemGroup`s, which MSBuild resolves with a duplicate-item warning — and `Assets\AppIcon.ico` is both `<ApplicationIcon>` and a `Content` item with `CopyToOutputDirectory=PreserveNewest`, shipping a redundant copy of the icon next to the exe. `ModuleBuild.targets` additionally copies every module's output (DLL **and** its large dependencies: ONNX Runtime, sqlite-vec, LibGit2Sharp) into a single shared `Modules/` folder, which is what makes F-16's unload bug reachable. Fix: drop the duplicate `None` item and the `Content` copy; give each module a subfolder under `Modules/`.

### F-40 — IDE-only VS packages are referenced by the shipping WinForms app · **Medium** · `Kaeo LLM Proxy.csproj`

`Microsoft.VisualStudio.Utilities` (17.14.40264) is a Visual Studio shell library; it is what drags the vulnerable transitive `MessagePack` 2.5.192 that the very next line has to force-upgrade to 3.1.8. The project's other comment says the host "uses Serilog directly (startup logging); the other packages now live in the Core / Infrastructure / Services libraries" — so the VS dependency looks vestigial (it is also the reason `Microsoft.VisualStudio.Threading.Analyzers` shows up in the build). Carrying a shell assembly in the app means: a permanent CVE-tracking obligation, a version pin tied to VS 2026 servicing, and — because the module ALC defers to the host for any assembly in `deps.json` (F-18) — those VS types get unified into every module for free.

Suggested fix: confirm nothing actually uses `Microsoft.VisualStudio.Utilities`, and remove it (the analyzer can be a `PrivateAssets=all` development dependency instead); then delete the `MessagePack` override if it was only needed by the VS package chain.

### F-41 — `InternalsVisibleTo` is declared on the host but the internals under test live in the libraries · **Low** · `Kaeo LLM Proxy.csproj`

The test files reach directly into `internal` types of `Kaeo.LlmProxy.Services` (`OllamaProxyHandler.ExtractDeclaredToolNames`, `TransformNonStreamingChatBody`, `PassthroughToolCallExtractionTests`) and `Kaeo.LlmProxy.Core.Models`. The only `InternalsVisibleTo` in the project file shown is in the host project; the grants for `Core`/`Services`/`Infrastructure` must live in their own files (or in `Directory.Build.props`) — worth verifying, because a missing grant would mean those tests are compiling against public surface only, and `ModelMappingCloneTests`' use of `EnsureId`/`AssignNewId`/`ContextSummarizeModelId` proves internal access is expected.

## Coverage gaps — read these next

Not covered by this pass, in descending expected value:

1. `MainForm.cs` (+ `TrayApplicationContext.cs`, the five dialogs, `CapabilityDetector.cs`) — the largest unreviewed surface in the app. Look for: `Invoke`/`BeginInvoke` on disposed controls, `async void` handlers without `try/catch` (the WinForms rule the repo states), grid→model commit races against the running proxy, and whether `MainForm` caches `AppLogger.SysLog` (re-`Initialize` swaps that static — F-21 — so a cached reference would silently stop updating the System Logs tab).
2. Services not read: `AutoCompactionService.cs`, `StatisticsService.cs`, `PerformanceService.cs`, `DebugNotes.cs`, `Mcp/McpServerService.cs`, `Mcp/McpApiExplorer.cs`, `Translation/OpenAiOutbound.cs`, `Translation/OllamaRequestTranslation.cs`, and the unread thirds of `OllamaProxyHandler.cs` (`HandleChatAsync`/`StreamChatToOllamaAsync`, `ExtractXmlToolCalls`/`MapTools`/`MapMessage`, `HandleCompactAsync`/`HandleManualCompactAsync`, `NormalizeRequestBody` body, `ShouldSkipRequestHeader`, `ThinkTagExtractor` edge cases). Specifically verify that the `/api/chat` (Ollama-native) route applies the same declared-name tool-call filter the OpenAI route does (F-01/F-02).
3. Modules: everything in Code Vector Store (path handling for per-repository mirror paths — check for traversal outside `DataDirectory`; the ONNX/tokenizer/index-engine lifecycle; lazy database open), Web Search providers (`DomainPolicyService` rule matching — suffix vs. registrable-domain confusion is the classic bug; `HtmlTextExtractor` on untrusted HTML), SSH (`SshTools` command execution surface, `SshRepository` settings persistence).
4. `Kaeo VS Extension` / `MeaiAdapter` (above).
5. Test suite bodies — `PassthroughToolCallExtractionTests` and `ContextCompactionTests` look solid; what is missing is a reflection-based completeness test for `ModelMapping.Clone()` (F-12) and any negative test for the fail-open tool-call path (F-01) with an unparseable body.

---

# Prioritized fix list

| Order | Item | Why first |
|---|---|---|
| 1 | F-01 + F-02 + F-03 + F-25 | One coherent defect class: an unbindable function part reaching Copilot. Fail-closed default, guard on every `/v1/*` route, buffer nameless streamed fragments, and make inbound `Content-Encoding` impossible to misread |
| 2 | F-26 + F-08 | Unauthenticated network exposure of tool execution (MCP) and of upstream keys (proxy) — bind-vs-auth policy in one place |
| 3 | F-04 + F-35 | Atomic settings write + backup-on-corrupt; then the early `Save()` paths stop being a data-loss risk |
| 4 | F-13 | Module trust boundary: per-module database, per-session credential grants, pinned assembly hash |
| 5 | F-37 | Route every outbound fetch through `NetworkSafety`, re-validate per redirect hop, pin the validated IP |
| 6 | F-14 + F-15 | Move log persistence off the request path and prune `system_logs`; resolves the F-21 dead-settings confusion too |
| 7 | F-06 | Either light up the four flags (incl. an off-switch for auto-compaction) or delete the dead IR/manual-compact code |
| 8 | F-16 → F-19 | Module loading/unloading correctness cluster: per-module folders, reference-counted directories, synchronized `_loadedModules`, version-aware unification |
| 9 | F-20 + F-27 + F-28 | `busy_timeout`; bound the XML tool-call buffer and flush on EOS; single-pass body decoding |
| 10 | F-05, F-07, F-09, F-22, F-23, F-24, F-29 – F-31, F-35b, F-36, F-38, F-39, F-40, F-41 | Hardening and hygiene, safe to batch |

---

# Appendix

## Previously reviewed and resolved (do not re-report)

2026-07-14 pass items #1–#23: single-`AppDatabase` ownership, `_handler`/`_stats` disposal in `TrayApplicationContext.Dispose`, `async void OnExit` guard, `UpdateSettings` `HttpClient` swap race, `PruneExpired` queue race, `IsContextOverflowErrorAsync` double-read, `AppSettings.Load`/`Save` error handling, `DeleteOlderThan` N+1, missing `timestamp_utc` index, `ProxyServer.Dispose` awaiting the listen task, `_listener!` null-forgiving, `IsStreamingJsonBody` full-DOM parse, `HandlePsAsync` no-op await, sensitive-field redaction scope, indentation, unused `BuildHttpClient` parameter, redundant usings, `PerformanceService.Sample` empty catch, per-request `HttpClient` in the test console + `FetchUpstreamModelsAsync`, unconditional WAL pragma, `AddLog` soft cap (accepted by design), redundant `Stop()` in `StopAsync`.
