#!/usr/bin/env bash
# demo15-failure.sh — live demo of P15's failure-visibility task (Task 3).
#
# The P15 orchestrator runs a workflow whose second remote hop is the P09
# InventoryAgent at http://localhost:5199. This demo KILLS that hop:
#
#   1. Starts ONLY the P15 DiagnosisAgentService (:5200). The P09 inventory
#      service (:5199) is deliberately NOT started — if something is already
#      listening there the script refuses to run, because a live inventory
#      service would make the demo dishonest.
#   2. Runs the HARDWARE scenario (scenario B). Diagnosis flags NEEDS-HARDWARE,
#      the conditional edge routes to InventoryAgent, and the A2A call dies on
#      a connection refused. In the streaming event loop the orchestrator
#      already uses, that surfaces as a WorkflowErrorEvent; the wrapper
#      exception names the failing A2A endpoint and the process exits
#      non-zero (PROPAGATE — no retry, no next-scenario).
#   3. Runs the SOFTWARE scenario (scenario A) as a separate invocation. The
#      conditional edge never routes to :5199, so the workflow completes
#      successfully even with the inventory service still down — the contrast
#      is the point.
#
# Prereqs: Ollama on localhost:11434 with glm-5.3-flash:cloud (checked up
# front; an honest error, not a hang, if it is missing).
#
# Usage:
#   scripts/demo15-failure.sh

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ORCHESTRATOR="$ROOT/src/P15.OrchestratorHost"
DIAGNOSIS="$ROOT/src/P15.DiagnosisAgentService"
DIAG_BASE="http://localhost:5200"
TMP="$(mktemp -d /tmp/demo15.XXXXXX)"
DIAG_PID=""

cleanup() {
    if [ -n "$DIAG_PID" ]; then
        # `dotnet run` spawns a child that owns the port — take both down.
        pkill -P "$DIAG_PID" 2>/dev/null || true
        kill "$DIAG_PID" 2>/dev/null || true
        wait "$DIAG_PID" 2>/dev/null || true
    fi
    rm -rf "$TMP"
}
trap cleanup EXIT

say()  { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
fail() { printf '\033[31mFAIL: %s\033[0m\n' "$*" >&2; exit 1; }

# --- 1. Prereqs ----------------------------------------------------------------

say "P15 — kill-service failure demo (inventory service DOWN, diagnosis service UP)"

tags="$(curl -s --max-time 5 localhost:11434/api/tags || true)"
[ -n "$tags" ] || fail "Ollama is not reachable at localhost:11434 — start it with 'ollama serve'"
printf '%s' "$tags" | grep -Eq '"glm-5.3-flash:cloud"' \
    || fail "model 'glm-5.3-flash:cloud' not present in Ollama — run: ollama pull glm-5.3-flash:cloud"
info "Ollama present with glm-5.3-flash:cloud"

if curl -s -o /dev/null --max-time 2 "$DIAG_BASE/.well-known/agent-card.json"; then
    fail "something is already listening on :5200 — stop it so the script can own the DiagnosisAgentService"
fi
if lsof -nP -iTCP:5199 -sTCP:LISTEN 2>/dev/null | grep -q LISTEN; then
    fail "port :5199 is LISTENING — stop the P09 InventoryAgentService first; the demo needs the inventory service DOWN"
fi
info "ports :5200 free (script will own the diagnosis service), :5199 free (inventory service DOWN)"

# --- 2. Build, start DiagnosisAgentService only ---------------------------------

say "— building the two P15 projects (compile errors fail fast here)"
dotnet build "$ORCHESTRATOR" >/dev/null || fail "orchestrator build failed"
dotnet build "$DIAGNOSIS" >/dev/null || fail "DiagnosisAgentService build failed"

info "starting DiagnosisAgentService on :5200 (inventory service stays DOWN)"
ASPNETCORE_URLS="$DIAG_BASE" dotnet run --no-build --project "$DIAGNOSIS" \
    >"$TMP/diagnosis.log" 2>&1 &
DIAG_PID=$!
waited=0
until curl -s -o /dev/null --max-time 2 "$DIAG_BASE/.well-known/agent-card.json"; do
    if ! kill -0 "$DIAG_PID" 2>/dev/null; then
        echo "DiagnosisAgentService exited during startup — last log lines:" >&2
        tail -n 20 "$TMP/diagnosis.log" >&2
        exit 1
    fi
    if [ "$waited" -ge 90 ]; then
        echo "DiagnosisAgentService did not become reachable within 90s" >&2
        exit 1
    fi
    sleep 1
    waited=$((waited + 1))
done
info "DiagnosisAgentService is up (pid $DIAG_PID, log $TMP/diagnosis.log)"

# --- 3. Run 1: hardware scenario against the dead :5199 hop ---------------------

say "— run 1: scenario B (hardware) with the inventory service STOPPED"
set +e
dotnet run --no-build --project "$ORCHESTRATOR" -- B >"$TMP/failure.log" 2>&1
FAILURE_EXIT=$?
set -e
cat "$TMP/failure.log"

info "exit code: $FAILURE_EXIT (PROPAGATE says a failed scenario must exit non-zero)"
[ "$FAILURE_EXIT" -ne 0 ] || fail "the hardware run exited 0 — the failure was swallowed, which is exactly what this task exists to prevent"

grep -q "\[workflow error\]" "$TMP/failure.log" \
    || fail "no [workflow error] line — the failure did not surface as a WorkflowErrorEvent in the streaming event loop"
grep -q "\[FAILED\]" "$TMP/failure.log" \
    || fail "no [FAILED] diagnostics — the orchestrator did not report the failed scenario"
# Pinned to the ERROR path (review fix): the [discovery failed] line also
# mentions :5199, so a whole-log grep would be satisfied without the failure
# ever surfacing. Only the [failed hop] annotation or a [FAILED] diagnostic
# line naming 5199 proves the failure path itself carries the endpoint.
grep -Eq "^\[(failed hop|FAILED)\].*5199" "$TMP/failure.log" \
    || fail "neither the [failed hop] annotation nor a [FAILED] line names the A2A endpoint on port 5199 — the operator cannot tell WHICH hop died"
say "OK: hardware run failed visibly — WorkflowErrorEvent + exception path naming the :5199 A2A endpoint"

# --- 4. Run 2: software scenario, inventory still down --------------------------

say "— run 2: scenario A (software) with the inventory service STILL down"
dotnet run --no-build --project "$ORCHESTRATOR" -- A >"$TMP/software.log" 2>&1 \
    || fail "the software run failed even though the conditional edge should never contact :5199 (log: $TMP/software.log)"
cat "$TMP/software.log"
grep -q "InventoryAgent (:5199) SKIPPED by the conditional edge" "$TMP/software.log" \
    || fail "the software run did not report the inventory hop as SKIPPED"
if grep -q "\[workflow error\]" "$TMP/software.log"; then
    fail "the software run produced a workflow error — unexpected"
fi
say "OK: software run succeeded with the inventory service down — the conditional edge never contacted :5199"

say "demo complete: failure visible where it happened (WorkflowErrorEvent + endpoint-naming exception, non-zero exit),"
say "software path unaffected. Transcripts kept at $TMP (removed on exit — copy first if you need them)."
