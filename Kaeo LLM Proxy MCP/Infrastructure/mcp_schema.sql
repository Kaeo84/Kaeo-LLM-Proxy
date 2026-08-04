-- Kaeo LLM Proxy MCP module baseline schema.
-- Applied through IModuleDatabase.ExecuteSchemaScript during module initialization.
-- Idempotent: safe to run on every startup.

-- Key/value settings for the MCP server host (enabled, listen address, port, auth).
CREATE TABLE IF NOT EXISTS mcp_server_settings (
	key TEXT PRIMARY KEY,
	value TEXT NOT NULL
);

-- Web search provider catalog with per-provider settings.
-- Exactly one row per provider kind; enabled flag toggles participation in queries.
CREATE TABLE IF NOT EXISTS mcp_web_search_providers (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	name TEXT NOT NULL UNIQUE,
	is_enabled INTEGER NOT NULL DEFAULT 0,
	endpoint TEXT NOT NULL,
	credential_name TEXT NULL
);

-- Domain allow/deny rules for web_search/web_fetch.
-- rule_type: 0 = allow, 1 = deny. An allowlist with any entry restricts everything else.
CREATE TABLE IF NOT EXISTS mcp_domain_rules (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	rule_type INTEGER NOT NULL,
	pattern TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_mcp_domain_rules_unique
	ON mcp_domain_rules (rule_type, pattern);

-- Key/value settings for the Web Search feature (tool toggles, result limits, timeouts,
-- response size cap, allow-local-network opt-in).
CREATE TABLE IF NOT EXISTS mcp_web_search_settings (
	key TEXT PRIMARY KEY,
	value TEXT NOT NULL
);
