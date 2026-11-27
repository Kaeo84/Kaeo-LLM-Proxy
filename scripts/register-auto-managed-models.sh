#!/bin/sh
# Seeds ONE auto-managed model mapping so Kaeo LLM Proxy starts discovering the rest of that
# upstream's models by itself. Re-running is safe: it never creates a second seed.
#
# Preferred: a service account's API key, so no dashboard password is needed anywhere.
#
#   KAEO_API_KEY='kaeo-...' ./register-auto-managed-models.sh
#
# The key's user needs the Registration Read-Write role (Users tab -> Add user). Falling back to a
# dashboard login also works:
#
#   KAEO_PASSWORD='your-dashboard-password' ./register-auto-managed-models.sh
#
# Every value below can be overridden from the environment:
#   KAEO_DASHBOARD      dashboard base URL            (http://127.0.0.1:11435)
#   KAEO_API_KEY        service account API key       (none; takes priority over the login)
#   KAEO_USER           dashboard username            (admin)
#   KAEO_PASSWORD       dashboard password            (none)
#   KAEO_UPSTREAM       upstream base URL             (http://127.0.0.1:11434)
#   KAEO_UPSTREAM_TYPE  1=OpenAI 2=Ollama 3=Anthropic (2)
#   KAEO_CREDENTIAL     stored credential name for the upstream (none)
#   KAEO_SEED_NAME      proxy name for the seed       (kaeo-auto)
#   KAEO_SEED_MODEL     model id on the upstream      (first id the upstream reports)
#   KAEO_SEED_HIDDEN    1 = keep the seed out of /v1/models (0)
set -eu

DASHBOARD=${KAEO_DASHBOARD:-http://127.0.0.1:11435}
API_KEY=${KAEO_API_KEY:-}
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

# One call shape for both credentials: the API key rides in an Authorization header, the dashboard
# login in a session cookie. The body lands in $WORK/out.json; the HTTP status comes back on stdout.
request() {
	method=$1
	path=$2
	shift 2
	if [ -n "$API_KEY" ]; then
		set -- -H "Authorization: Bearer $API_KEY" "$@"
	else
		set -- -b "$COOKIE" "$@"
	fi
	curl -sS -o "$WORK/out.json" -w '%{http_code}' -X "$method" "$@" "$DASHBOARD$path"
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

if [ -n "$API_KEY" ]; then
	printf 'Using a service account API key against %s.\n' "$DASHBOARD"
elif [ -n "$PASSWORD" ]; then
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
	printf 'No KAEO_API_KEY or KAEO_PASSWORD set: assuming dashboard sign-in is switched off.\n'
fi

status=$(request GET /api/mappings) || die "Could not reach the dashboard at $DASHBOARD."
if [ "$status" != "200" ]; then
	die "Could not list model mappings (HTTP $status): $(cat "$WORK/out.json")
HTTP 401 means the key or the session was refused; HTTP 403 means the account lacks Registration
Read-Write (a dashboard login needs Model Admin instead)."
fi
mappings=$(cat "$WORK/out.json")

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
	status=$(request POST /api/mappings -H 'Content-Type: application/json' \
		--data-binary "@$WORK/seed.json") \
		|| die "Could not reach the dashboard at $DASHBOARD."
	if [ "$status" != "200" ]; then
		die "Creating the seed mapping failed (HTTP $status): $(cat "$WORK/out.json")"
	fi
	printf 'Created seed mapping "%s" -> %s (%s).\n' "$SEED_NAME" "$SEED_MODEL" "$UPSTREAM"
fi

printf 'Running a discovery scan ...\n'
status=$(request POST /api/mappings/discover) || die "Could not reach the dashboard at $DASHBOARD."
if [ "$status" != "200" ]; then
	die "The discovery scan failed (HTTP $status): $(cat "$WORK/out.json")
See the dashboard Models tab for the reason."
fi
cat "$WORK/out.json"