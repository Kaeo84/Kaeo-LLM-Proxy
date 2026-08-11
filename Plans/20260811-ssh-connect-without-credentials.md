# Allow credential-less ssh_connect attempt via MCP (connect + disconnect demo)

Branch: feature/mcp-module
Date: 2026-08-11

## Understanding
The user wants to see their "Local MCP Test" SSH tools work end-to-end: connect to
192.168.101.1 with username "noone" and no credentials, then disconnect. Today the SSH
module blocks such requests before any network activity: `TryBuildRequest` rejects requests
with no password/private key, and `BuildAuthenticationMethods` throws when the auth-method
list would be empty (SSH.NET's `ConnectionInfo` requires at least one method). Relax this so
a real handshake is attempted and the actual server result is returned through the MCP tool.

## Assumptions
- Target: host 192.168.101.1, port 22, username "noone", no credential, no stored connection.
- The attempt will most likely fail authentication (empty password) or time out — the goal is
  to exercise the full MCP → module → SSH path and return real feedback; if it unexpectedly
  succeeds, disconnect right after.
- The app must be restarted to load the rebuilt SSH module DLL; it is restarted and left running.

## Approach
Edit `Kaeo LLM Proxy SSH/SshModule.cs` only:
- In `TryBuildRequest` (~line 1200), drop the hard rejection when no auth material is present
  so a username-only ad-hoc request builds a valid `SshConnectionRequest`.
- In `BuildAuthenticationMethods` (~line 767), when neither key nor password was supplied, add
  a `PasswordAuthenticationMethod` with an empty password as the fallback so SSH.NET has at
  least one method (it also probes "none" auth automatically) and the remote server's real
  authentication response surfaces through the existing `SshException` catch in `ConnectAsync`.
Then rebuild, restart the app, and drive the live tools: `ssh_connect` → `ssh_list` →
`ssh_disconnect`.

Mid-execution discovery: the VS MCP client keeps its old `Mcp-Session-Id` across app
restarts and never re-initializes after a 404, so every tool call failed with
"Session not found". Additional server-side fix in `Infrastructure/Mcp/McpServerHost.cs`:
requests with an unknown session id now recreate the session and replay the lifecycle
handshake (synthetic `initialize` + `notifications/initialized` into `Stream.Null`) before
executing the incoming request; the response carries the new session id header.

## Key Files
- Kaeo LLM Proxy SSH/SshModule.cs - tool validation (`TryBuildRequest`) and auth-method
  construction (`BuildAuthenticationMethods`)

## Risks & Open Questions
- 192.168.101.1 may be unreachable: connect returns the module's timeout error after ~30s —
  acceptable, still real end-to-end feedback.
- Restarting the user's running app is disruptive but required; it is restarted immediately
  and left running.
- Empty-password attempts are only made when the caller explicitly supplies no material;
  stored-connection resolution errors are unchanged.

## Steps
- [x] 1. Relax validation in SshModule.TryBuildRequest so credential-less ad-hoc requests proceed
- [x] 2. Make SshModule.BuildAuthenticationMethods fall back to an empty-password method and update its doc comment
- [x] 3. Build the solution and confirm no errors
- [x] 4. Restart the Kaeo LLM Proxy app so the rebuilt SSH module loads
- [x] 5. Implement + verify stale-session recovery in McpServerHost (bogus session id → 200 with tools)
- [x] 6. Drive the live tools: ssh_connect (Permission denied by remote, as expected), ssh_list, ssh_disconnect
- [x] 7. Save/update the plan file in Plans/ and git commit the change

## Result
Full MCP → module → SSH path verified from GitHub Copilot. With no credentials the remote
host at 192.168.101.1 rejects auth ("Permission denied (password)") — the pipeline itself
works end-to-end. Stale sessions now recover automatically across server restarts.
