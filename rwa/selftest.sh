#!/usr/bin/env bash
# rwa acceptance self-test, run entirely against a synthetic protocol root.
#
# Spec 1.4 (git-bug e3d04c7). This is the reproducible half of the acceptance
# evidence: every check below runs without RimWorld, because the interesting
# behaviour of a protocol client is its failure modes and those are far easier
# to produce deterministically than to provoke in a live game. `fakebench.py`
# stands in for Poller.cs (same inbox rules, same envelope, same id
# sanitisation) and each failure is a flag rather than a race.
#
# What this file deliberately does NOT cover, and what therefore has to be
# demonstrated against the real bench by whoever owns it:
#   * the live round-trip (ping / status / advance against a running game);
#   * `rwa watch on|off` revealing and hiding Hyprland's special:rwagent, and
#     mangohud re-reading its config to re-cap a running game.
# The hyprctl half is stubbed out here by unsetting HYPRLAND_INSTANCE_SIGNATURE:
# toggling a special workspace on a live desktop is a side effect a test has no
# business having, and an empty special:rwagent would just cover the user's
# screen with nothing.
#
# Usage:  ./selftest.sh            (temp root, cleaned up)
#         KEEP=1 ./selftest.sh     (leave the synthetic root for inspection)

set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
# exported: several checks shell out through `sh -c` to prove a pipeline
# works exactly as it is documented, and those are child processes.
export RWA="$HERE/rwa"
export FAKE="$HERE/fakebench.py"

export TMP
TMP="$(mktemp -d "${TMPDIR:-/tmp}/rwa-selftest.XXXXXX")"
export RWA_ROOT="$TMP/AutoRimmer"
export RWA_TRANSCRIPTS="$TMP/transcripts"
mkdir -p "$RWA_ROOT"

PASS=0; FAIL=0; SERVER=""
OUT=""; RC=0

cleanup() {
    [ -n "$SERVER" ] && kill "$SERVER" 2>/dev/null
    if [ "${KEEP:-0}" = "1" ]; then echo; echo "kept: $TMP"; else rm -rf "$TMP"; fi
}
trap cleanup EXIT

section() { printf '\n\n=== %s %s\n' "$1" "$(printf '=%.0s' $(seq $((66 - ${#1}))))"; }
ok()   { PASS=$((PASS+1)); printf '  PASS  %s\n' "$1"; }
bad()  { FAIL=$((FAIL+1)); printf '  FAIL  %s\n' "$1"; }

# run <want-exit> <label> -- <command...>   : run it, show it, check the exit code
run() {
    local want="$1" label="$2"; shift 3
    printf '\n$ %s\n' "$*"
    OUT="$("$@" 2>&1)"; RC=$?
    printf '%s\n' "$OUT" | sed 's/^/  | /'
    if [ "$RC" = "$want" ]; then ok "$label (exit $RC)"; else bad "$label (exit $RC, wanted $want)"; fi
}
has()  { if printf '%s' "$OUT" | grep -qF -- "$1"; then ok "output contains: $1"; else bad "output MISSING: $1"; fi; }
hasre(){ if printf '%s' "$OUT" | grep -qE -- "$1"; then ok "output matches: $1"; else bad "output does NOT match: $1"; fi; }
lacks(){ if printf '%s' "$OUT" | grep -qF -- "$1"; then bad "output should NOT contain: $1"; else ok "output lacks: $1"; fi; }

serve() {  # serve [extra fakebench flags…]
    [ -n "$SERVER" ] && { kill "$SERVER" 2>/dev/null; wait "$SERVER" 2>/dev/null; SERVER=""; }
    python3 "$FAKE" serve --root "$RWA_ROOT" "$@" >"$TMP/fakebench.log" 2>&1 &
    SERVER=$!
    for _ in $(seq 40); do [ -f "$RWA_ROOT/status.json" ] && return 0; sleep 0.1; done
    echo "fakebench failed to start:"; cat "$TMP/fakebench.log"; exit 1
}
stop() { [ -n "$SERVER" ] && { kill "$SERVER" 2>/dev/null; wait "$SERVER" 2>/dev/null; SERVER=""; }; }

echo "rwa selftest — synthetic root $RWA_ROOT"
"$RWA" --version


section "1. root resolution"
run 0 "explicit --root wins" -- "$RWA" root
has "$RWA_ROOT"
run 0 "root --json names every candidate it tried" -- env -u RWA_ROOT "$RWA" root --json
hasre '"candidates":'
hasre '_RimWorld-Agent/config/unity3d'
hasre 'SaveData/AutoRimmer'
hasre 'AppData/LocalLow'


section "2. the three liveness states (plus menu), no server running"
python3 "$FAKE" status --root "$RWA_ROOT" --state down
run 3 "no heartbeat at all => down, and it says how to start the game" -- "$RWA" status --pretty
has "status.json is missing"
has "run-agent.sh --quicktest"
has "_RimWorld-Testing and never the MP install"

python3 "$FAKE" status --root "$RWA_ROOT" --state stale
run 3 "an hour-old heartbeat => down" -- "$RWA" status --pretty
hasre 'heartbeat is 3[0-9]{2}[0-9]\.[0-9]+s old'
has "run-agent.sh"

python3 "$FAKE" status --root "$RWA_ROOT" --state starved
run 0 "live heartbeat, 1.7 fps => starved, NOT 'not running'" -- "$RWA" status --pretty
has "STARVED"
has "frame-bound"
has "FINDINGS 4b"
lacks "start it:"

python3 "$FAKE" status --root "$RWA_ROOT" --state stalled
run 3 "fresh heartbeat, main thread dead => stalled" -- "$RWA" status --pretty
has "STALLED"
has "render_unfocused"

python3 "$FAKE" status --root "$RWA_ROOT" --state menu
run 0 "at the main menu => menu, told to load a save" -- "$RWA" status --pretty
has "MENU"
has "load a save"

python3 "$FAKE" status --root "$RWA_ROOT" --state down
run 3 "a command is refused fast when the bench is down, and is NOT written" -- "$RWA" ping --pretty
has "rwa-game-down"
if [ -z "$(ls -A "$RWA_ROOT/commands" 2>/dev/null)" ]; then
    ok "inbox is empty — no ghost command left to surface as stale-on-restart"
else
    bad "a command file was written to a dead bench: $(ls "$RWA_ROOT/commands")"
fi


section "3. stale-on-restart: an inbox file that predates the session"
mkdir -p "$RWA_ROOT/commands"
printf '{"id":"ghost","op":"ping","args":{}}' > "$RWA_ROOT/commands/ghost.json"
serve
sleep 1
run 0 "the bench came up over a non-empty inbox" -- "$RWA" journal --list
if [ -f "$RWA_ROOT/results/ghost.json" ] && grep -q 'stale-on-restart' "$RWA_ROOT/results/ghost.json"; then
    ok "results/ghost.json: $(cat "$RWA_ROOT/results/ghost.json")"
else
    bad "no stale-on-restart result for the planted inbox file"
fi


section "4. round trip: ping / status / version"
run 0 "ping" -- "$RWA" ping --pretty
has "pong: true"
run 0 "ping --echo, arg reaches the verb" -- "$RWA" ping --echo hello --json
has '"echo": "hello"'
hasre '"ok": true'
run 0 "the machine envelope is exactly the mod's bytes" -- "$RWA" ping --json
hasre '"id":.*"op": "ping".*"ok": true.*"state".*"sid"'
run 0 "verbs" -- "$RWA" verbs
has "advance"
has "map-view"
run 0 "status --probe round-trips the off-thread status verb" -- "$RWA" status --probe --pretty
has "probe   status verb answered"
run 0 "status --sample observes the tick counter over time" -- "$RWA" status --sample 1 --pretty
has "sample"
has "paused"


section "5. argument syntax"
run 0 "types are guessed: number, bool, string" -- \
    "$RWA" ping --json --args-json '{}' --n 500 --flag --word Normal --pos 120,130
run 0 "…and land in the command file as JSON of the right type" -- \
    sh -c 'cat "$RWA_TRANSCRIPTS"/*/*-ping/cmd.json | tail -1'
run 0 "dotted keys nest, repetition builds arrays, :json passes raw" -- \
    "$RWA" advance --json --until.event.type death --until.event.contains Xitral \
        --types letter --types death --rect:json '[10,20,5,5]' --timeout 3
run 0 "the command file shows the nesting" -- \
    sh -c 'ls -d "$RWA_TRANSCRIPTS"/*/*-advance | tail -1 | xargs -I{} cat {}/cmd.json'
has '"until": {"event": {"type": "death", "contains": "Xitral"}}'
has '"types": ["letter", "death"]'
has '"rect": [10, 20, 5, 5]'
run 2 "an id the mod would sanitise is rejected up front, not silently rewritten" -- \
    "$RWA" ping --id 'has.a.dot'
has "Poller.Sanitize"


section "6. the error taxonomy, one code at a time"
run 1 "unknown-op — and the mod lists what it knows" -- "$RWA" nosuchverb
has "unknown-op"
has "known ops:"
run 1 "bad-args" -- "$RWA" advance
has "bad-args"
has "advance needs 'ticks' or 'until'"
run 1 "bad-json (hand-written command file)" -- sh -c '
    printf "not json at all" > "$RWA_ROOT/commands/.tmp-bj" ;
    mv "$RWA_ROOT/commands/.tmp-bj" "$RWA_ROOT/commands/bj.json" ;
    sleep 2 ; cat "$RWA_ROOT/results/bj.json" ; exit 1'
has "bad-json"
stop
serve --game-loaded false
run 1 "no-active-game at the menu, from the mod not from us" -- "$RWA" ping
has "no-active-game"
stop
serve --advance-secs 8
"$RWA" advance --ticks 999999 --timeout 0 --no-transcript >/dev/null 2>&1 &
ADV=$!
sleep 2
run 1 "busy: one long-running op at a time" -- "$RWA" ping
has "busy"
run 0 "…but the off-thread verbs still answer during an advance" -- "$RWA" status --probe --pretty
has "probe   status verb answered"
run 0 "pause is the brake pedal and interrupts it" -- "$RWA" pause --pretty
has "was_advancing: true"
wait "$ADV"


section "7. client-side failures wear the same envelope"
stop
serve --answer silent
run 4 "timeout: consumed but never answered" -- "$RWA" ping --timeout 2 --json
has '"code": "rwa-timeout"'
hasre '"ok": false'
stop
serve --answer mangle
run 1 "a mangled result file is reported, not crashed on" -- "$RWA" ping --json
has '"code": "rwa-bad-result"'
stop
serve --advance-secs 30
# The command has to still be in flight when the bench dies, or this proves
# nothing — so it is an advance, killed two seconds in.
( sleep 2 ; kill "$SERVER" ) &
run 3 "the bench dying mid-command is 'game down', not 'timeout'" -- \
    "$RWA" advance --ticks 999999 --timeout 0 --stale-secs 3 --json
has "rwa-game-down"
has "stale-on-restart at the next launch"
SERVER=""


section "8. journal and tail"
serve
"$RWA" advance --ticks 10 --no-transcript >/dev/null 2>&1
run 0 "journal reads the ndjson directly — no game needed, no round trip" -- \
    "$RWA" journal --pretty
has "session"
hasre "^--- [0-9]+ events, last_seq"
run 0 "…and --verb takes the same read through the protocol" -- "$RWA" journal --verb --json
hasre '"op": "journal".*"events"'
run 0 "--type filters, --since resumes" -- "$RWA" journal --type session --since 0 --pretty
has "boot"
run 0 "--list enumerates sessions" -- "$RWA" journal --list
hasre '\.ndjson'
run 0 "tail --once prints the last n and exits" -- "$RWA" tail --once -n 3
run 0 "tail --json is NDJSON, one event per line" -- "$RWA" tail --once -n 2 --json
hasre '^\{"seq"'


section "9. transcripts and replay"
export RWA_RUN="acceptance"
"$RWA" ping --quiet >/dev/null
"$RWA" version --quiet >/dev/null
"$RWA" digest --quiet >/dev/null
"$RWA" advance --ticks 250 --quiet >/dev/null
run 0 "a scripted session leaves a complete run dir" -- find "$RWA_TRANSCRIPTS/acceptance" -type f
has "meta.json"
has "log.ndjson"
has "cmd.json"
has "result.json"
run 0 "log.ndjson is one line per command" -- cat "$RWA_TRANSCRIPTS/acceptance/log.ndjson"
run 0 "the transcript replays against the bench" -- "$RWA" replay acceptance --run replayed --pretty
hasre "4 sent, 0 failed"
run 0 "…and the replay is itself a transcript" -- \
    sh -c 'ls "$RWA_TRANSCRIPTS/replayed"'
unset RWA_RUN
run 0 "with no --run, the run dir is the game session id" -- \
    sh -c '"$RWA" ping --quiet >/dev/null; ls "$RWA_TRANSCRIPTS" | grep -E "^[0-9]{8}T[0-9]{6}$"'


section "10. jq pipelines (the documented ones, pasted verbatim)"
run 0 "one alert label per line" -- \
    sh -c '"$RWA" digest --json | jq -r ".data.alerts.active[] | \"\(.priority)\t\(.label)\""'
has "Need colonist beds"
run 0 "colonists below a mood threshold" -- \
    sh -c '"$RWA" digest --json | jq -r ".data.colonists[] | select(.mood_pct < 60) | .name"'
has "Xitral"
run 0 "advance, then everything the journal saw during it" -- \
    sh -c 'seq=$("$RWA" journal --json --limit 1 --since 99999 | jq .data.last_seq);
           "$RWA" advance --ticks 200 --json | jq -c ".data | {reason, ticks_elapsed, avg_tps}";
           "$RWA" journal --json --since "$seq" | jq -r ".data.events[] | \"\(.tick)\t\(.type)\""'
run 0 "error handling is one shape whoever failed" -- \
    sh -c '"$RWA" nosuchverb --json | jq -r "if .ok then \"ok\" else .error.code end"'
has "unknown-op"
run 1 "…and the exit code is the mod's verdict, not the pipeline's" -- \
    "$RWA" nosuchverb --quiet


section "11. watch (fps cap only; hyprctl deliberately stubbed out)"
BENCH="$TMP/_RimWorld-Agent"
mkdir -p "$BENCH/config" "$BENCH/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer"
printf 'fps_limit=30\nno_display=1\n' > "$BENCH/config/mangohud-agent.conf"
cp "$RWA_ROOT/status.json" "$BENCH/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer/"
WROOT="$BENCH/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer"
run 0 "watch on rewrites fps_limit in the file MangoHud is watching" -- \
    env -u HYPRLAND_INSTANCE_SIGNATURE "$RWA" watch on --root "$WROOT"
run 0 "the config now caps at 60 and still hides the HUD" -- cat "$BENCH/config/mangohud-agent.conf"
has "fps_limit=60"
has "no_display=1"
run 0 "an explicit cap: a flag VALUE after a positional word is not eaten" -- \
    env -u HYPRLAND_INSTANCE_SIGNATURE "$RWA" watch on --fps 144 --root "$WROOT"
run 0 "…and it took" -- cat "$BENCH/config/mangohud-agent.conf"
has "fps_limit=144"
run 0 "watch off puts it back to the unwatched default" -- \
    env -u HYPRLAND_INSTANCE_SIGNATURE "$RWA" watch off --root "$WROOT"
run 0 "…verified in the file" -- cat "$BENCH/config/mangohud-agent.conf"
has "fps_limit=30"
run 2 "usage errors go to stderr, never onto stdout as non-JSON" -- \
    sh -c '"$RWA" --json > "$TMP/stdout.txt" 2>/dev/null; rc=$?; [ -s "$TMP/stdout.txt" ] && echo "STDOUT NOT EMPTY"; exit $rc'
lacks "STDOUT NOT EMPTY"
run 0 "bare watch reports state without changing anything" -- \
    env -u HYPRLAND_INSTANCE_SIGNATURE "$RWA" watch --root "$WROOT" --json
hasre '"action": "show"'
run 0 "the bench dir is derived from the protocol root, not hardcoded" -- \
    env -u HYPRLAND_INSTANCE_SIGNATURE "$RWA" watch --root "$WROOT" --json
has "$BENCH"


section "results"
printf '\n  %d passed, %d failed\n\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
