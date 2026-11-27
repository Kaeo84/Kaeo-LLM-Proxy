#!/bin/sh
# Seeds ONE auto-managed model mapping so Kaeo LLM Proxy starts discovering the rest of that
# upstream's models by itself. Re-running is safe: it never creates a second seed.
#
#   KAEO_PASSWORD='your-dashboard-password' ./register-auto-managed-models.sh
#
# Every value below can be overridden from the environment:
#   KAEO_DASHBOARD      dashboard base URL            (http://127.0.0.1:11435)
#   KAEO_USER           dashboard username            (admin)
#   KAEO_PASSWORD       dashboard password            (required unless dashboard sign-in is off)
#   KAEO_UPSTREAM       upstream base URL             (http://127.0.0.1:11434)
#   KAEO_UPSTREAM_TYPE  1=OpenAI 2=Ollama 3=Anthropic (2)
#   KAEO_CREDENTIAL     stored credential name for the upstream (none)
#   KAEO_SEED_NAME      proxy name for the seed       (kaeo-auto)
#   KAEO_SEED_MODEL     model id on the upstream      (first id the upstream reports)
#   KAEO_SEED_HIDDEN    1 = keep the seed out of /v1/models (0)
set -eu

DASHBOARD=${KAEO_DASHBOARD:-http://127.0.0.1:11435}
USER_NAME=${KAEO_USER:-admin}
PASSWORD=${KAEO_PASSWORD:-}
UPSTREAM=${KAEO_UPSTREAM:-http://127.0.0.1:11434}
UPSTREAM_TYPE=${KAEO_UPSTREAM_TYPE:-2}
CREDENTIAL=${KAEO_CREDENTIAL:-}
SEED_NAME=${KAEO_SEED_NAME:-kaeo-auto}
SEED_MODEL=${KAEO_SEED_MODEL:-}
SEED_HIDDEN=${KAEO_SEED_HIDDEN:-0}

DASHBOARD=${DASHBOARD%/}
UPSTREAM=${UPSTREAM%/}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
COOKIE="$WORK/cookies.txt"

die() {
	printf '%s\n' "$*" >&2
	exit 1
}

# The seed must name a model the upstream really serves, otherwise discovery switches it off once
# the offline grace period elapses. Ask the upstream which models it has rather than guessing.
if [ -z "$SEED_MODEL" ]; then
	printf 'Looking up a model id on %s ... ' "$UPSTREAM"
	upstream_models=$(curl -sS -f --max-time 10 "$UPSTREAM/v1/models" 2>/dev/null || true)
	SEED_MODEL=$(printf '%s' "$upstream_models" | grep -o '"id":"[^"]*"' | head -n 1 | cut -d '"' -f 4 || true)
	if [ -z "$SEED_MODEL" ]; then
		printf 'failed\n'
		die "Could not read a model id from $UPSTREAM/v1/models.
Set KAEO_SEED_MODEL to a model the upstream serves, or fix KAEO_UPSTREAM."
	fi
	printf '%s\n' "$SEED_MODEL"
fi

if [ -n "$PASSWORD" ]; then
	cat > "$WORK/login.json" <<EOF
{"username":"$USER_NAME","password":"$PASSWORD"}
EOF
	status=$(curl -sS -o "$WORK/login-out.json" -w '%{http_code}' \
		-c "$COOKIE" -H 'Content-Type: application/json' \
		--data-binary "@$WORK/login.json" "$DASHBOARD/api/auth/login") \
		|| die "Could not reach the dashboard at $DASHBOARD."
	if [ "$status" != "200" ]; then
		die "Dashboard sign-in as '$USER_NAME' failed (HTTP $status): $(cat "$WORK/login-out.json")"
	fi
	printf 'Signed in to %s as %s.\n' "$DASHBOARD" "$USER_NAME"
else
	printf 'KAEO_PASSWORD is empty: assuming dashboard sign-in is switched off.\n'
fi

mappings=$(curl -sS -f -b "$COOKIE" "$DASHBOARD/api/mappings") \
	|| die "Could not list model mappings. HTTP 401 means the dashboard needs a password; HTTP 403 means this account is neither Admin nor Model Admin."

if printf '%s' "$mappings" | grep -q "\"OllamaName\":\"$SEED_NAME\""; then
	printf 'Seed mapping "%s" already exists, leaving it alone.\n' "$SEED_NAME"
else
	if [ "$SEED_HIDDEN" = "1" ]; then hidden=true; else hidden=false; fi
	if [ -n "$CREDENTIAL" ]; then
		credential_json="\"credentialName\": \"$CREDENTIAL\","
	else
		credential_json=""
	fi
	cat > "$WORK/seed.json" <<EOF
{
  "OllamaName": "$SEED_NAME",
  "LlamaCppName": "$SEED_MODEL",
  "upstreamUrl": "$UPSTREAM",
  "upstreamType": $UPSTREAM_TYPE,
  $credential_json
  "autoManaged": true,
  "isEnabled": true,
  "hidden": $hidden
}
EOF
	curl -sS -f -o "$WORK/seed-out.json" -b "$COOKIE" -H 'Content-Type: application/json' \
		--data-binary "@$WORK/seed.json" "$DASHBOARD/api/mappings" \
		|| die "Creating the seed mapping failed: $(cat "$WORK/seed-out.json" 2>/dev/null)"
	printf 'Created seed mapping "%s" -> %s (%s).\n' "$SEED_NAME" "$SEED_MODEL" "$UPSTREAM"
fi

printf 'Running a discovery scan ...\n'
curl -sS -f -b "$COOKIE" -X POST "$DASHBOARD/api/mappings/discover" \
	|| die 'The discovery scan failed; see the dashboard Models tab for the reason.'
printf '\n'