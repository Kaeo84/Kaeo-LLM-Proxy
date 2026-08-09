# SSH MCP Module + Credential Store Keys/Certs + Open Connections Table

## Understanding
Create a new "SSH Command" MCP module for Kaeo LLM Proxy that lets AI clients SSH to computers, run commands, and get responses over maintained connections with a configurable idle timeout. Additionally: (1) extend the host's central credential store to hold a username, an SSH private key (passkey), and a certificate alongside the existing secret; (2) thread the MCP session's client IP through to module tool targets so the module can show which computer opened each SSH connection; (3) all SSH-specific GUI (stored connections CRUD, settings, open connections table with Refresh) lives inside the module-injected config page.

## Assumptions
- Contracts changes are acceptable without compat shims (unreleased app; both modules live in this solution).
- SSH.NET 2026.0.0 is the SSH library; private key auth is fully supported. User-certificate support verified at implementation; graceful degradation if unsupported.
- Stored connections + settings tables live in the shared app DB via IModuleDatabase with `mcp_`-prefixed names, following Web Search module conventions.
- Module-injected GUI only: the host dashboard gets no SSH-specific controls.
- IPv4 preferred for client IPs (IPv4-mapped IPv6 addresses are unmapped).

## Steps
- [x] 1. Extend module contracts for credential material and session info
  - ISecretProvider.cs: add CredentialMaterial record + ResolveCredential(string)
  - new McpSessionInfo.cs: public sealed record McpSessionInfo(string SessionId, string? ClientAddress)
  - IMcpToolModule.cs: CreateMcpToolTargets(McpSessionInfo session)
  - save plan to Plans/
- [x] 2. Extend host credential model and persistence
  - AppSettings.cs: StoredCredential + Username/PrivateKey/Certificate
  - AppDatabase.cs: credentials DDL columns, MigrateCredentialsTable wired into InitializeDatabase, LoadCredentials/SaveCredentials updated
- [x] 3. Extend host encrypt/decrypt paths
  - Program.cs ResolvePassphrase hasEncrypted + TryDecryptAllSecrets over all secret fields
  - MainForm.cs TryEncryptCredentialsForSave + BtnEditCredential_Click field copies
  - check remaining StoredCredential construction sites
- [x] 4. Extend CredentialDialog with username, private key, certificate fields
  - multiline key/cert boxes with Import-from-file buttons; validation: name + at least one of secret/private key
- [x] 5. Thread MCP session info through the host
  - ModuleSecretProvider.ResolveCredential; ModuleHost.GetMcpToolTargets(session); McpServerOptionsFactory.Build(session); McpServerHost factory Func<McpSessionInfo, McpServerOptions>, CreateSession(clientAddress) from RemoteEndPoint (IPv4 preferred)
- [x] 6. Update Web Search module to the new contract signature
- [x] 7. Create the SSH module project and register it in the solution
  - csproj (SSH.NET 2026.0.0, contracts ProjectReference Private=false, MCP/Serilog compile-time, embedded schema) + slnx entry
- [x] 8. Create SSH schema, models, and repository
  - ssh_schema.sql (mcp_ssh_connections, mcp_ssh_settings); SshStoredConnection/SshSettings models; SshRepository (key/value + CRUD)
- [x] 9. Implement SshConnectionManager
  - connect/exec/disconnect, connection keys (stored name or user@host:port), idle sweep timer with per-connection override, snapshot + ConnectionsChanged event, credential-based auth (key + optional cert, password)
- [x] 10. Implement SshTools MCP tools
  - ssh_connect / ssh_exec (auto-connect) / ssh_disconnect / ssh_list with live enabled checks, opener session+client IP capture, output truncation
- [x] 11. Implement SshModule entry point
  - IKaeoModule + IMcpToolModule + IRunnableModule (StartAsync starts sweep, StopAsync closes all) + IHelpModule help text
- [x] 12. Implement SSH config page and stored connection dialog
  - SshConfigPage: settings group, stored connections CRUD, Open Connections table with Refresh + Disconnect (all module-injected GUI)
  - SshConnectionDialog: name/host/port/username/credential pick/idle override
- [x] 13. Build the solution and fix all compile errors
  - root csproj needed Compile/EmbeddedResource/None Remove for the SSH folder (duplicate assembly attribute errors); ExitStatus nullable; DesignerSerializationVisibility attributes; CopyLocalLockFileAssemblies so Renci.SshNet.dll + BouncyCastle.Cryptography.dll ship beside the module DLL
- [x] 14. Update the plan file checklist and commit all changes with a summary message

## Risks & Open Questions
- SSH.NET client (user) certificate authentication API surface must be verified during implementation; fallback is key/password auth with the stored cert unused + logged warning.
- SSH.NET async method availability (ConnectAsync / SshCommand.ExecuteAsync) verified during implementation; fallback wraps sync calls.
- Module consumers must import the SSH module DLL from a folder containing its sibling dependencies (SSH.NET.dll) — existing module convention.
- SSH host keys are accepted as presented by SSH.NET defaults; the help page documents this trust model.
