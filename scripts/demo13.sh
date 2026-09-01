#!/usr/bin/env bash
# demo13.sh — live demo of P13.StreamingApproval's approval round trip.
#
# Streams a message that makes the agent call the gated `delete_ticket` tool,
# catches the `event: approval` SSE frame, votes POST /approvals/{id}, and
# prints the resumed turn. Because MAF 1.19's UseToolApproval ends the run at
# the FIRST gated call (one pause per resume round), the script loops:
# stream -> approval frame -> vote -> stream ... until no approval remains.
#
# Usage:
#   scripts/demo13.sh                                  # starts the app on :5139
#   BASE_URL=http://localhost:5139 scripts/demo13.sh   # against a running app
#
# The app seeds a "Password reset loop" ticket at startup (fresh GUID per
# restart once the previous one is tombstoned), so the script reads the ticket
# id from the store files under bin/ instead of guessing.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/P13.StreamingApproval"
BASE_URL="${BASE_URL:-http://localhost:5139}"
CONV="demo13-$(date +%s)"
TMP="$(mktemp -d /tmp/demo13.XXXXXX)"
APP_PID=""
JQ="$(command -v jq || true)"

cleanup() {
    if [ -n "$APP_PID" ]; then
        # `dotnet run` spawns a child that owns the port — take both down.
        pkill -P "$APP_PID" 2>/dev/null || true
        kill "$APP_PID" 2>/dev/null || true
        wait "$APP_PID" 2>/dev/null || true
    fi
    rm -rf "$TMP"
}
trap cleanup EXIT

say()  { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }

# --- JSON helpers: jq when present, sed/grep fallback otherwise --------------

json_field() {  # json_field <json-string> <field> — first value, plain text
    if [ -n "$JQ" ]; then
        printf '%s' "$1" | "$JQ" -r --arg f "$2" '.[$f] // empty' 2>/dev/null
    else
        printf '%s' "$1" \
            | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p" \
            | head -n 1
    fi
}

pretty() {  # pretty <json-string> — indented if jq exists, raw otherwise
    if [ -n "$JQ" ]; then
        printf '%s' "$1" | "$JQ" -S . 2>/dev/null || printf '%s\n' "$1"
    else
        printf '%s\n' "$1"
    fi
}

deltas() {  # deltas <sse-file> — assistant text from `data: {"delta":...}` frames
    if [ -n "$JQ" ]; then
        sed -n 's/^data: //p' "$1" | "$JQ" -r 'select(.delta != null) | .delta' 2>/dev/null \
            | tr -d '\n'
    else
        sed -n 's/^data: {"delta"://p' "$1" | sed 's/}$//' | tr -d '\n'
    fi
}

show_deltas() {  # like deltas, but silent when the turn carried no text
    local text
    text="$(deltas "$1")"
    if [ -n "$text" ]; then printf '  assistant: %s\n' "$text"; fi
}

first_approval() {  # first_approval <sse-file> — JSON of the first approval frame
    if [ -n "$JQ" ]; then
        sed -n 's/^data: //p' "$1" \
            | "$JQ" -s '[.[] | select(.requestId != null)][0] // empty' 2>/dev/null
    else
        sed -n 's/^data: //p' "$1" | grep '{"requestId"' | head -n 1
    fi
}

has_error_frame() { grep -q '^event: error' "$1"; }
error_detail()   { sed -n 's/^data: //p' "$1" | head -n 1; }

# --- 1. App up ----------------------------------------------------------------

say "P13 — streaming tool approval over SSE ($BASE_URL)"

app_up() { curl -s -o /dev/null --max-time 2 "$BASE_URL/" 2>/dev/null; }

if app_up; then
    info "app already running at $BASE_URL — using it"
else
    info "starting dotnet run --project src/P13.StreamingApproval ..."
    ASPNETCORE_URLS="$BASE_URL" dotnet run --project "$PROJECT" \
        >"$TMP/app.log" 2>&1 &
    APP_PID=$!
    waited=0
    until app_up; do
        if ! kill -0 "$APP_PID" 2>/dev/null; then
            echo "app exited during startup — last log lines:" >&2
            tail -n 20 "$TMP/app.log" >&2
            exit 1
        fi
        if [ "$waited" -ge 90 ]; then
            echo "app did not become reachable within 90s" >&2
            exit 1
        fi
        sleep 1
        waited=$((waited + 1))
    done
    info "app is up (pid $APP_PID, log $TMP/app.log)"
fi

# --- 2. Find the seeded ticket (a live one: not yet tombstoned) ---------------

tickets_file=$(find "$PROJECT"/bin -path '*/work/tickets.json' 2>/dev/null | head -n 1 || true)
deleted_file=$(find "$PROJECT"/bin -path '*/work/tickets-deleted.json' 2>/dev/null | head -n 1 || true)
if [ -z "$tickets_file" ]; then
    echo "ticket store not found under $PROJECT/bin — start the app once to seed it" >&2
    exit 1
fi

TICKET_ID=""
if [ -n "$JQ" ]; then
    # live = present in tickets.json and not in the tombstone list
    # (--slurpfile wraps the tombstone array one level deep, hence flatten)
    TICKET_ID="$("$JQ" -r '
        [.[] | select(.Title == "Password reset loop") | .Id as $id
              | select($tomb | flatten | index($id) | not)][0].Id // empty' \
        --slurpfile tomb "${deleted_file:-/dev/null}" "$tickets_file" 2>/dev/null || true)"
fi
if [ -z "$TICKET_ID" ]; then
    # sed fallback: first ticket id the tombstone file does not contain
    TICKET_ID="$(sed -n 's/.*"Id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tickets_file" \
        | while IFS= read -r id; do
              if [ -z "$deleted_file" ] || ! grep -q "$id" "$deleted_file"; then
                  printf '%s\n' "$id"
                  break
              fi
          done)"
fi
if [ -z "$TICKET_ID" ]; then
    echo "no live 'Password reset loop' ticket in the store — restart the app to reseed it" >&2
    exit 1
fi
info "conversation: $CONV"
info "target ticket: $TICKET_ID"

# --- 3. Stream turn 1: the gated delete ----------------------------------------

say "— turn 1: 'Delete ticket $TICKET_ID' (curl -N, text/event-stream)"
curl -sN --max-time 300 -X POST "$BASE_URL/conversations/$CONV/messages" \
    -H 'Content-Type: application/json' \
    -d "{\"text\":\"Delete ticket $TICKET_ID.\"}" >"$TMP/turn1.sse"
show_deltas "$TMP/turn1.sse"

# --- 4. Approval loop: one pause per resume round -------------------------------

round=1
turnfile="$TMP/turn1.sse"
while :; do
    approval="$(first_approval "$turnfile")"
    [ -n "$approval" ] || break

    request_id="$(json_field "$approval" requestId)"
    if [ -z "$request_id" ]; then
        echo "approval frame without a requestId — aborting" >&2
        exit 1
    fi
    say "— PAUSE #$round: agent requests approval (event: approval frame)"
    if [ -n "$JQ" ]; then
        pretty "$approval" | sed 's/^/    /'
    else
        printf '    %s\n' "$approval"
    fi

    say "— operator approves: POST /approvals/$CONV"
    if [ -n "$JQ" ]; then
        vote="$("$JQ" -nc --arg id "$request_id" \
            '{requestId:$id, approved:true, reason:"demo operator approved"}')"
    else
        vote="{\"requestId\":\"$request_id\",\"approved\":true,\"reason\":\"demo operator approved\"}"
    fi
    round=$((round + 1))
    turnfile="$TMP/turn$round.sse"
    curl -sN --max-time 300 -X POST "$BASE_URL/approvals/$CONV" \
        -H 'Content-Type: application/json' \
        -d "$vote" >"$turnfile"
    show_deltas "$turnfile"
done

# --- 5. Outcome ------------------------------------------------------------------

if has_error_frame "$turnfile"; then
    say "— stream ended with an error frame:"
    error_detail "$turnfile" | sed 's/^/    /'
fi
if [ -f "$deleted_file" ] && grep -q "$TICKET_ID" "$deleted_file"; then
    say "OK: ticket $TICKET_ID tombstoned in $(basename "$deleted_file") — the tool ran, and only after the vote"
else
    say "note: no approval round was needed (stream carried text only) — raw frames in $TMP"
fi
say "raw SSE transcripts: $TMP/turn*.sse"
