#!/usr/bin/env bash
# demo14.sh — live demo of P14.SemanticMemory's durable fact memory.
#
# Two separate PROCESSES, one fact file:
#   1. `dotnet run -- tell "<preference>"` — the agent answers, the tiny fact
#      extractor distills the turn into third-person facts, FactMemoryStore
#      saves p14-facts.json, the process exits. The T1 ChatHistoryMemoryProvider
#      baseline (in-process InMemoryVectorStore) dies with the process — that
#      is the point of the second run.
#   2. `dotnet run -- recall "<question>"` in a FRESH process — the fact store
#      loads p14-facts.json at startup and the remembered preference steers
#      the answer. The script asserts the answer mentions the preference.
#
# Prereqs: Ollama on localhost:11434 with `glm-5.3-flash:cloud` + `bge-m3`
# (checked up front; an honest error, not a hang, if they are missing).
#
# Usage:
#   scripts/demo14.sh                                        # default texts
#   scripts/demo14.sh "preference..." "recall question..."   # custom texts

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/P14.SemanticMemory"
PREF="${1:-Remember: I prefer email over phone for anything urgent.}"
QUESTION="${2:-What is the best way to reach me about an urgent outage?}"
SCRATCH="$(mktemp -d /tmp/demo14.XXXXXX)"

cleanup() {
    # Both app phases run in the foreground and exit on their own — the only
    # thing to clean is the scratch dir.
    rm -rf "$SCRATCH"
}
trap cleanup EXIT

say()  { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
fail() { printf '\033[31mFAIL: %s\033[0m\n' "$*" >&2; exit 1; }

# --- 1. Prereqs: Ollama and the two models -------------------------------------

say "P14 — semantic memory across processes (Ollama localhost:11434)"

tags="$(curl -s --max-time 5 localhost:11434/api/tags || true)"
[ -n "$tags" ] || fail "Ollama is not reachable at localhost:11434 — start it with 'ollama serve'"
for model in "glm-5.3-flash:cloud" "bge-m3"; do
    # `ollama pull bge-m3` lists it as bge-m3:latest — accept any tag suffix.
    printf '%s' "$tags" | grep -Eq "\"${model}(:[A-Za-z0-9._-]+)?\"" \
        || fail "model '$model' not present in Ollama — run: ollama pull $model"
done
info "models present: glm-5.3-flash:cloud, bge-m3"

# --- 2. Build once, then run both phases with --no-build -----------------------

say "— building src/P14.SemanticMemory (compile errors fail fast here)"
dotnet build "$PROJECT" >/dev/null || fail "build failed"

# The app writes p14-facts.json relative to its working directory, so both
# phases run from the same scratch dir — that IS the persistence medium.
cd "$SCRATCH"

# --- 3. Process 1: state a preference, quit -------------------------------------

say "— process 1: tell"
info "preference: $PREF"
dotnet run --no-build --project "$PROJECT" -- tell "$PREF" | tee tell.log
[ -f p14-facts.json ] || fail "process 1 exited without writing p14-facts.json — nothing can persist"

say "— what the extractor persisted (p14-facts.json):"
sed -n 's/.*"Text"[: ]*"\([^"]*\)".*/  fact: \1/p' p14-facts.json | sort -u || true

# --- 4. Process 2: recall in a fresh process ------------------------------------

say "— process 2: recall (fresh process — no chat history, no in-memory store)"
info "question: $QUESTION"
dotnet run --no-build --project "$PROJECT" -- recall "$QUESTION" | tee recall.log

# --- 5. Verdict ------------------------------------------------------------------

# The asserted keyword is derived from the preference (first significant
# word), so a custom "$1" asserts its own content instead of always grepping
# "email". The default preference yields "email"; a preference with no
# significant word falls back to it.
KEYWORD="$(printf '%s' "$PREF" \
    | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9 \n' | tr -s ' ' '\n' \
    | awk 'length($0) >= 4 && $0 !~ /^(remember|prefer|please|that|this|with|want|need|really|always|never|when|what|about)$/ {print; exit}')"
KEYWORD="${KEYWORD:-email}"
info "asserting answer mentions: $KEYWORD"

if grep -qi "$KEYWORD" recall.log; then
    say "OK: a fresh process answered from memory it was never told in-process —"
    say "    the fact store carried the preference across the restart."
else
    say "FAIL: the fresh-process answer did not mention the remembered preference."
    say "      (live model run — the answer above is the raw evidence; re-run to retry)"
    exit 1
fi
