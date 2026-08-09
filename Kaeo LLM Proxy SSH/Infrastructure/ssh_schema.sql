-- Kaeo LLM Proxy SSH module baseline schema.
-- Applied through IModuleDatabase.ExecuteSchemaScript during module initialization.
-- Idempotent: safe to run on every startup.

-- Named SSH connections the AI client can open by name.
-- credential_name references the host's central credential store, which supplies the
-- username/password or private key/certificate used to authenticate.
-- idle_timeout_seconds: per-connection override; 0 = use the module-wide default.
CREATE TABLE IF NOT EXISTS mcp_ssh_connections (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	name TEXT NOT NULL UNIQUE,
	host TEXT NOT NULL,
	port INTEGER NOT NULL DEFAULT 22,
	username TEXT NOT NULL,
	credential_name TEXT NULL,
	idle_timeout_seconds INTEGER NOT NULL DEFAULT 0
);

-- Key/value settings for the SSH feature (tool toggles, idle timeout, command timeout,
-- output size cap).
CREATE TABLE IF NOT EXISTS mcp_ssh_settings (
	key TEXT PRIMARY KEY,
	value TEXT NOT NULL
);
