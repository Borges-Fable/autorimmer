#!/usr/bin/env bash
# Acceptance for spec 1.8 — "advance drives the game clock: unpause to play,
# pause to stop" (git-bug b8785e8).
#
# Run it against a LIVE _RimWorld-Agent bench with a colony loaded and the game
# paused. It speaks the raw file protocol — numbered envelopes into
# commands/<id>.json, one result read back from results/<id>.json — so it needs
# nothing but bash and jq, and every command it sends is left on disk as
# evidence. `rwa` is the friendlier way to poke at the same surface by hand;
# this script deliberately does not depend on it.
#
#   accept/1.8-game-clock-advance.sh [PROTOCOL_ROOT] [--manual]
#
# PROTOCOL_ROOT defaults to $AUTORIMMER_ROOT, else the `root` field of
# status.json is not discoverable without a root, so pass it. It is the
# save-data AutoRimmer/ directory, e.g.
#   ~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer
#
# --manual enables step 20, which needs a human at the bench window.
#
# BENCH PREREQUISITES, and read these before disbelieving a failure:
#
# 1. `automaticPauseMode=Never` (FINDINGS section 3, seeded by
#    profile/make-profile-agent). With any other value
#    `LetterStack.ReceiveLetter` calls `Find.TickManager.Pause()` on letter
#    arrival, and since 1.8 the game really is running — so a plain
#    `advance {ticks:N}` will legitimately come back `reason:"stalled"` with
#    `stalled.cause:"external-pause"` the first time a letter lands. That is
#    the new code reporting the truth, not a bug in it.
#
# 2. A quiet colony. This script advances roughly 45000 ticks. If a REAL
#    timing-out letter (a trade or quest offer) reaches its last tick during a
#    `{ticks:N}` step, that step correctly returns `reason:"dialog"` instead of
#    `"ticks"` and reads as a failure here. Clear the stack
#    (`journal-selftest {"steps":["dialogs-clear"],"drop_fixture_letters":false}`
#    leaves real letters alone; answer them with 3.5 when it exists) and re-run
#    that step. A step that fails this way names the window in
#    `force_pause_windows`, so it is never ambiguous.

set -uo pipefail

ROOT="${1:-${AUTORIMMER_ROOT:-}}"
MANUAL=0
for a in "$@"; do [ "$a" = "--manual" ] && MANUAL=1; done
[ "${ROOT:-}" = "--manual" ] && ROOT="${AUTORIMMER_ROOT:-}"

if [ -z "${ROOT:-}" ] || [ ! -d "$ROOT/commands" ]; then
  echo "usage: $0 <protocol-root> [--manual]"
  echo "  <protocol-root> is the save-data AutoRimmer/ directory (it contains commands/)"
  exit 2
fi
command -v jq >/dev/null || { echo "jq is required"; exit 2; }

CMDS="$ROOT/commands"
RES="$ROOT/results"
STATUS="$ROOT/status.json"

PASS=0; FAIL=0; SKIP=0
FAILED_STEPS=""

hr()   { printf '%s\n' "----------------------------------------------------------------------"; }
note() { printf '      %s\n' "$*"; }

ok()   { PASS=$((PASS+1)); printf '  PASS  %s\n' "$*"; }
bad()  { FAIL=$((FAIL+1)); FAILED_STEPS="$FAILED_STEPS
    - $*"; printf '  FAIL  %s\n' "$*"; }
skip() { SKIP=$((SKIP+1)); printf '  SKIP  %s\n' "$*"; }

# ---------------------------------------------------------------- transport --

# send_async <id> <op> <args-json>
send_async() {
  local id="$1" op="$2" args="$3"
  rm -f "$RES/$id.json"
  printf '{"id":%s,"op":%s,"args":%s}\n' \
    "$(jq -Rn --arg v "$id" '$v')" "$(jq -Rn --arg v "$op" '$v')" "$args" \
    > "$CMDS/.$id.json.tmp"
  mv -f "$CMDS/.$id.json.tmp" "$CMDS/$id.json"
}

# await <id> [timeout-seconds] -> RESULT holds the parsed envelope
RESULT=""
await() {
  local id="$1" timeout="${2:-120}" waited=0
  RESULT=""
  while [ ! -f "$RES/$id.json" ]; do
    sleep 0.25
    waited=$((waited+1))
    if [ $((waited/4)) -ge "$timeout" ]; then
      RESULT='{"id":"'"$id"'","ok":false,"error":{"code":"client-timeout","detail":"no result file after '"$timeout"'s"}}'
      return 1
    fi
  done
  # The poller writes the file atomically (tmp + rename), but re-read once on a
  # parse failure rather than trusting that across filesystems.
  RESULT="$(cat "$RES/$id.json" 2>/dev/null)"
  if ! printf '%s' "$RESULT" | jq -e . >/dev/null 2>&1; then
    sleep 0.5
    RESULT="$(cat "$RES/$id.json" 2>/dev/null)"
  fi
  return 0
}

# send <id> <op> <args-json> [timeout]
send() { send_async "$1" "$2" "$3"; await "$1" "${4:-120}"; }

# ----------------------------------------------- git-bug 722c951: the escape --
#
# `advance` has TWO new default-on guards, and both are right for a play loop
# and wrong for a fixture harness:
#
#   * it REFUSES (ok:false, error.code "unread-journal") when the previous
#     advance journaled events that no `journal` call has read, and
#   * it HALTS (reason:"casualty") when an own-faction pawn goes down or dies
#     while time is running.
#
# This script is not a play loop. It advances ~45000 ticks to measure THROUGHPUT
# and halt behaviour, it never reads the journal in between, and steps 09/12
# deliberately advance into a self-opening letter. Without an opt-out every
# advance after the first that journaled anything would come back refused and
# the throughput numbers below would be measuring a refusal.
#
# So the opt-out lives HERE, in ONE wrapper, and not at the call sites: an
# inline `unread_ok` is indistinguishable to the next reader from one somebody
# added to get a red check green. The reason string names this file, so
# `journal --types action` on the bench says which harness turned the guard off
# and why. Both escapes are per-call and journaled as an act by the mod
# (session 13's threat-pardon precedent).
ESCAPE='accept/1.8-game-clock-advance.sh: fixture harness, not a play loop - it advances to measure throughput and halt behaviour, and does not read the journal between advances'

# with_escape <args-json> -> the same JSON with both per-call escapes added
with_escape() {
  printf '%s' "$1" | jq -c --arg r "$ESCAPE" '. + {unread_ok:$r, through_casualties:$r}'
}

# adv <id> <args-json> [timeout]        — every advance in this file goes here
adv() { send "$1" advance "$(with_escape "$2")" "${3:-120}"; }

# adv_async <id> <args-json>
adv_async() { send_async "$1" advance "$(with_escape "$2")"; }

# q <jq-filter> -> value from the last RESULT
q() { printf '%s' "$RESULT" | jq -r "$1" 2>/dev/null; }

# eq <label> <jq-filter> <expected>
eq() {
  local label="$1" filter="$2" want="$3" got
  got="$(q "$filter")"
  if [ "$got" = "$want" ]; then ok "$label ($filter = $got)"
  else bad "$label: $filter = '$got', expected '$want'"; fi
}

# truthy <label> <jq-filter>   (the filter must evaluate to true)
truthy() {
  local label="$1" filter="$2" got
  got="$(q "$filter")"
  if [ "$got" = "true" ]; then ok "$label"
  else bad "$label: $filter = '$got'"; fi
}

show() { printf '      %s\n' "$(q "$1")"; }

# ------------------------------------------------------- status.json sampler --
#
# The headline acceptance is that the game is GENUINELY unpaused while an
# advance runs, and status.json is where that is observable from outside. The
# sampler tails the heartbeat while a long advance is in flight.

SAMPLE_PID=""
sample_start() {
  local out="$1"
  : > "$out"
  ( while :; do cat "$STATUS" 2>/dev/null >> "$out"; printf '\n' >> "$out"; sleep 0.2; done ) &
  SAMPLE_PID=$!
}
sample_stop() {
  if [ -n "$SAMPLE_PID" ]; then
    kill "$SAMPLE_PID" 2>/dev/null
    wait "$SAMPLE_PID" 2>/dev/null
    SAMPLE_PID=""
  fi
}

TMP="$(mktemp -d)"
trap 'sample_stop; rm -rf "$TMP"' EXIT

# =============================================================================
echo "AutoRimmer spec 1.8 acceptance — advance drives the game clock"
echo "root: $ROOT"
hr

# --- 01  preflight -----------------------------------------------------------
echo "01  preflight: a game is loaded and the clock is stopped"
send a18-01 status '{}' 30 || true
eq  "game loaded"            '.data.gameLoaded'  true
eq  "starts paused"          '.data.paused'      true
eq  "starts at speed Paused" '.data.speed'       "Paused"
truthy "no force-pausing window is already up" '.data.forcePause == null'
# since_seq past the end and limit 1: the verb reads the whole file (nothing
# matches, so it never hits the limit break) and last_seq is the true maximum.
# `{"limit":1}` alone would stop reading at the second line and report ITS seq.
send a18-01b journal '{"since_seq":999999999,"limit":1}' 30 || true
START_SEQ="$(q '.data.last_seq')"
START_SEQ="${START_SEQ%.*}"
case "$START_SEQ" in ''|*[!0-9]*) START_SEQ=0 ;; esac
note "journal seq at start: $START_SEQ"
hr

# --- 02  the headline: 20000 ticks, genuinely unpaused ----------------------
echo "02  advance {ticks:20000} — unpaused while it runs, paused when it returns"
sample_start "$TMP/sample-02.txt"
adv a18-02 '{"ticks":20000}' 300
sample_stop
eq  "ok"                       '.ok'                    true
eq  "reason"                   '.data.reason'           "ticks"
eq  "ran at Ultrafast"         '.data.speed'            "Ultrafast"
eq  "speed source"             '.data.speed_source'     "default"
eq  "nominal tps"              '.data.speed_nominal_tps' 900
eq  "returns paused"           '.data.paused_on_exit'   true
truthy "actual ticks >= 20000"          '.data.ticks_elapsed >= 20000'
truthy "overshoot within the bound"     '.data.overshoot <= .data.overshoot_bound'
eq  "overshoot bound is mult*2 = 30"    '.data.overshoot_bound' 30
truthy "no frame ran more than mult*2"  '.data.max_ticks_in_frame <= 30'
truthy "no speed set was refused"       '(.data.speed_set_refused // []) | length == 0'
truthy "nothing stalled"                '.data.stalled == null'
note "ticks_elapsed=$(q '.data.ticks_elapsed')  overshoot=$(q '.data.overshoot')  avg_tps=$(q '.data.avg_tps')  wall=$(q '.data.wall_seconds')s"
note "slower_spans=$(q '.data.slower_spans | tostring')"

# The status.json evidence: while it ran, the game reported a NON-Paused speed.
UNPAUSED_SAMPLES="$(grep -c '"paused":false' "$TMP/sample-02.txt" 2>/dev/null || echo 0)"
SPEEDS_SEEN="$(tr ',' '\n' < "$TMP/sample-02.txt" | grep -o '"speed":"[A-Za-z]*"' | sort -u | tr '\n' ' ')"
ADV_SEEN="$(grep -c '"advance":{"id":"a18-02"' "$TMP/sample-02.txt" 2>/dev/null || echo 0)"
if [ "$UNPAUSED_SAMPLES" -gt 0 ]; then ok "status.json showed paused:false while it ran ($UNPAUSED_SAMPLES samples)"
else bad "status.json never showed paused:false — the game was not genuinely unpaused"; fi
case "$SPEEDS_SEEN" in
  *Ultrafast*) ok "status.json showed a non-Paused speed: $SPEEDS_SEEN" ;;
  *)           bad "status.json never showed speed Ultrafast (saw: $SPEEDS_SEEN)" ;;
esac
if [ "$ADV_SEEN" -gt 0 ]; then ok "status.json carried the in-flight advance block ($ADV_SEEN samples)"
else bad "status.json never carried advance{id:a18-02}"; fi

send a18-02b status '{}' 30 || true
eq  "clock stopped afterwards"  '.data.paused' true
eq  "speed is Paused afterwards" '.data.speed' "Paused"
hr

# --- 03..06  the four-speed tps table ---------------------------------------
echo "03-06  the four speeds, ~10s each. This REPLACES spec 1.3's tps table."
echo "       measured avg_tps is the only truth: TimeSlower clamps any speed to"
echo "       ~60 after a threat and Superfast doubles to 720 when"
echo "       NothingHappeningInGame() (TickManager.TickRateMultiplier)."
TPS_TABLE=""
speed_case() {
  local id="$1" speed="$2" name="$3" ticks="$4" nominal="$5" bound="$6" lo="$7" hi="$8"
  adv "$id" "{\"ticks\":$ticks,\"speed\":\"$speed\"}" 300
  eq  "$speed: ok"                 '.ok'                  true
  eq  "$speed: reason"             '.data.reason'         "ticks"
  eq  "$speed: speed as asked"     '.data.speed'          "$name"
  eq  "$speed: speed source"       '.data.speed_source'   "speed"
  eq  "$speed: nominal tps"        '.data.speed_nominal_tps' "$nominal"
  eq  "$speed: overshoot bound"    '.data.overshoot_bound' "$bound"
  eq  "$speed: returns paused"     '.data.paused_on_exit' true
  truthy "$speed: overshoot within the bound" '.data.overshoot <= .data.overshoot_bound'
  local measured spans
  measured="$(q '.data.avg_tps')"
  spans="$(q '.data.slower_spans | tostring')"
  TPS_TABLE="$TPS_TABLE
  $(printf '%-10s nominal %5s   measured %10.1f   ticks %6s   wall %6ss   slower_spans %s' \
      "$speed" "$nominal" "$measured" "$(q '.data.ticks_elapsed')" "$(q '.data.wall_seconds')" "$spans")"
  # Band, not equality. A stretch inside a slower_span legitimately runs at 60.
  if awk -v m="$measured" -v lo="$lo" -v hi="$hi" 'BEGIN{exit !(m>=lo && m<=hi)}'; then
    ok "$speed: measured $measured tps is within [$lo, $hi]"
  elif [ "$spans" != "[]" ]; then
    skip "$speed: measured $measured tps is outside [$lo, $hi] but slower_spans is $spans — TimeSlower clamped it, which is now honoured rather than overridden"
  else
    bad "$speed: measured $measured tps outside [$lo, $hi] with no slower_span to explain it"
  fi
}
#            id       speed     name        ticks nominal bound   lo    hi
speed_case a18-03 normal    Normal        600   60      2      40    75
speed_case a18-04 fast      Fast         1800  180      6     120   220
speed_case a18-05 superfast Superfast    3600  360     24     240   800
speed_case a18-06 ultrafast Ultrafast    9000  900     30     500  1000
# Superfast's upper band is 800, not 400: TickRateMultiplier returns 12 rather
# than 6 while NothingHappeningInGame(), which doubles it to ~720 live and
# mid-advance. That is vanilla's behaviour and the spike measured it (FINDINGS
# section 6, "360 awake, 718-739 asleep").
hr
echo "  MEASURED TPS TABLE (paste this into the closing comment):$TPS_TABLE"
hr

# --- 07  until:letter still halts, and the halt names the event -------------
echo "07  until:{letter} — halts on the letter, halted_seq names the journal line,"
echo "    and the halt tick is within the bounded overshoot of the event tick"
send a18-07a journal-selftest '{"steps":["letter"],"letter_delay_ticks":300}' 60
eq  "letter armed" '.ok' true
adv a18-07 '{"until":{"letter":true},"timeout_ticks":20000,"speed":"fast"}' 300
eq  "reason"                  '.data.reason'   "letter"
eq  "returns paused"          '.data.paused_on_exit' true
truthy "halted_seq names a journal line" '.data.halted_seq > 0'
truthy "halted_on carries the letter def" '(.data.halted_on.def // "") | length > 0'
HALT_SEQ="$(q '.data.halted_seq')"
HALT_TICK="$(q '.data.tick')"
BOUND="$(q '.data.overshoot_bound')"
note "halted at tick $HALT_TICK on journal seq $HALT_SEQ (bound $BOUND)"
send a18-07b journal "{\"since_seq\":$((${HALT_SEQ%.*}-1)),\"limit\":1}" 60
eq  "the journal line at halted_seq is a letter" '.data.events[0].type' "letter"
EVT_TICK="$(q '.data.events[0].tick')"
if awk -v h="$HALT_TICK" -v e="$EVT_TICK" -v b="$BOUND" 'BEGIN{exit !(h>=e && h-e<=b)}'; then
  ok "halt tick $HALT_TICK is within $BOUND ticks of the event tick $EVT_TICK"
else
  bad "halt tick $HALT_TICK is not within $BOUND ticks of the event tick $EVT_TICK"
fi
hr

# --- 08..13  THE 1.7 REGRESSION ---------------------------------------------
echo "08-13  THE 1.7 REGRESSION. A timing-out letter opens ITSELF from"
echo "       LetterStack.LetterStackTick, stacking a forcePause dialog. Under"
echo "       1.3 our loop ticked straight through it and OpenAutomaticLetters"
echo "       was starved for the rest of the session. Under 1.8 the game"
echo "       force-pauses itself, advance reports reason:dialog, and a SECOND"
echo "       timing-out letter still arrives afterwards. The second letter IS"
echo "       the proof."

fixture_letter() {   # <id> <tag> -> OPENS_AT / NOW_TICK
  send "$1" journal-selftest "{\"steps\":[\"timeout-letter\"],\"timeout_ticks_letter\":600,\"letter_tag\":\"$2\"}" 60
  eq "timeout letter '$2' armed" '.ok' true
  OPENS_AT="$(q '.data.timeout_letter.opens_at_tick')"
  NOW_TICK="$(q '.data.timeout_letter.now_tick')"
  note "letter '$2': now=$NOW_TICK opens_at=$OPENS_AT"
}

dialog_advance() {   # <id> <tag>
  adv "$1" '{"ticks":20000,"speed":"fast"}' 300
  eq  "advance halted on the dialog"      '.data.reason'                        "dialog"
  eq  "advance returned paused"           '.data.paused_on_exit'                true
  truthy "a force-pausing window is named" '.data.force_pause_windows.count >= 1'
  truthy "the window is a Dialog_NodeTree" '[.data.force_pause_windows.windows[].type] | any(startswith("Dialog_NodeTree"))'
  truthy "the fixture letter '$2' is named" "[.data.force_pause_windows.letters[]?] | any(test(\"timeout letter $2\"))"
  truthy "it stopped WELL short of 20000"  '.data.ticks_elapsed < 1000'
  note "halted at tick $(q '.data.tick') after $(q '.data.ticks_elapsed') ticks; windows=$(q '.data.force_pause_windows.windows | map(.type) | tostring')"
}

echo "08  arm the FIRST timing-out letter"
fixture_letter a18-08 one
FIRST_OPENS="$OPENS_AT"
echo "09  advance into it"
dialog_advance a18-09 one
HALT="$(q '.data.tick')"
if awk -v h="$HALT" -v o="$FIRST_OPENS" 'BEGIN{exit !(h>=o && h-o<=6)}'; then
  ok "halted at the letter's own open tick ($HALT vs $FIRST_OPENS, bound 6)"
else
  bad "halted at $HALT, expected within 6 ticks of $FIRST_OPENS"
fi

echo "10  clear the dialog (the fixture escape hatch; 3.5 owns a real dismiss verb)"
send a18-10 journal-selftest '{"steps":["dialogs-clear"]}' 60
eq  "the force-pause stack is clear" '.data.dialogs_cleared.still_force_paused' false

echo "11  arm the SECOND timing-out letter"
fixture_letter a18-11 two
SECOND_OPENS="$OPENS_AT"
echo "12  advance again — if OpenAutomaticLetters had been starved, this letter"
echo "    would expire in silence and this advance would run all 20000 ticks"
dialog_advance a18-12 two
HALT2="$(q '.data.tick')"
if awk -v h="$HALT2" -v o="$SECOND_OPENS" 'BEGIN{exit !(h>=o && h-o<=6)}'; then
  ok "THE REGRESSION TEST: the second timing-out letter arrived and opened ($HALT2 vs $SECOND_OPENS)"
else
  bad "THE REGRESSION TEST: second letter halted at $HALT2, expected within 6 of $SECOND_OPENS"
fi

echo "13  clear again"
send a18-13 journal-selftest '{"steps":["dialogs-clear"]}' 60
eq  "the force-pause stack is clear" '.data.dialogs_cleared.still_force_paused' false
send a18-13b status '{}' 30 || true
truthy "status.json carries no forcePause block" '.data.forcePause == null'
hr

# --- 14..17  max_tps keeps working ------------------------------------------
echo "14-17  max_tps still works (1.4's CLI and the committed acceptance scripts"
echo "       pass it) and reports BOTH what was asked and what was set"

adv a18-14 '{"ticks":120,"max_tps":200}' 120
eq  "200 tps -> Fast"                 '.data.speed'                "Fast"
eq  "source is max_tps"               '.data.speed_source'         "max_tps"
eq  "effective tps is Fast's nominal" '.data.max_tps_effective'    180
eq  "the ask is echoed"               '.data.max_tps_asked'        200
eq  "the clamp is reported"           '.data.max_tps_clamped.to'   180
eq  "clamped by the speed ladder"     '.data.max_tps_clamped.by'   "speed-step"
eq  "returns paused"                  '.data.paused_on_exit'       true

adv a18-15 '{"ticks":60,"max_tps":5}' 120
eq  "5 tps -> Normal (nothing runs slower)" '.data.speed'              "Normal"
eq  "reported as a floor"                   '.data.max_tps_clamped.by' "floor"
eq  "effective tps"                         '.data.max_tps_effective'  60

adv a18-16 '{"ticks":600,"max_tps":5000}' 120
eq  "5000 tps -> Ultrafast"          '.data.speed'              "Ultrafast"
eq  "clamped by the config cap"      '.data.max_tps_clamped.by' "cap"
eq  "effective tps"                  '.data.max_tps_effective'  900

adv a18-17 '{"ticks":120,"max_tps":900}' 120
eq  "an exact ask is not clamped"    '.data.speed'              "Ultrafast"
truthy "no max_tps_clamped key when nothing moved" '.data.max_tps_clamped == null'
hr

# --- 18..19  busy-gating and the brake pedal --------------------------------
echo "18-19  pause still interrupts an advance in flight; busy-gating unchanged"
adv_async a18-18 '{"ticks":600000,"speed":"normal"}'
sleep 3
send a18-19a digest '{}' 60
eq  "a main-thread verb is refused"  '.ok'          false
eq  "with code busy"                 '.error.code'  "busy"
note "$(q '.error.detail')"
send a18-19b pause '{}' 60
eq  "pause reports it interrupted an advance" '.data.was_advancing' true
eq  "pause reports the clock stopped"         '.data.paused'        true
eq  "and the speed is Paused"                 '.data.speed'         "Paused"
await a18-18 60
eq  "the advance's own result says interrupted" '.data.reason'        "interrupted"
eq  "and it returned paused"                    '.data.paused_on_exit' true
truthy "it ran a nonzero number of ticks"       '.data.ticks_elapsed > 0'
note "interrupted after $(q '.data.ticks_elapsed') ticks"
hr

# --- 20  external pause -> reason:"stalled"  (MANUAL) -----------------------
echo "20  an EXTERNAL pause is reported, not left to time out silently"
if [ "$MANUAL" = "1" ]; then
  echo "    Reveal the bench window (rwa watch on), then press SPACE in it when"
  echo "    prompted. This is the only route the protocol has: unpause is"
  echo "    busy-gated during an advance and pause routes through Interrupt()."
  adv_async a18-20 '{"ticks":600000,"speed":"normal"}'
  sleep 2
  printf '    >>> PRESS SPACE IN THE RIMWORLD WINDOW NOW, then press Enter here: '
  read -r _
  await a18-20 120
  eq  "halted"                       '.data.reason'          "stalled"
  eq  "and named the cause"          '.data.stalled.cause'    "external-pause"
  eq  "and returned paused"          '.data.paused_on_exit'   true
  note "stalled: $(q '.data.stalled | tostring')"
else
  skip "20 external pause -> reason:stalled (re-run with --manual; needs a human on the space bar)"
fi
hr

# --- 21  a refused CurTimeSpeed set -> NOT DEMONSTRATED ---------------------
echo "21  a CurTimeSpeed set refused by PlayerCanControl"
skip "21 NOT DEMONSTRATED — no fixture can produce it from the protocol."
cat <<'EOF'
      PlayerCanControl is false only during ScreenFader.IsFading(), a gravship
      cutscene, or a landing-area confirmation (decompiled Verse/TickManager.cs
      PlayerCanControl, Verse/Game.cs PlayerHasControl). None is reachable from
      any shipped verb. The code path IS implemented and reports:

        - at start:  ok:false, error.code "cannot-set-speed", detail naming the
                     speed it wanted and the speed it observed;
        - mid-run:   data.speed_set_refused[] with {wanted, observed, why};
        - on exit:   data.paused_on_exit:false plus data.pause_refused, and a
                     pause debt every later frame retries until it takes.

      To DEMONSTRATE it, JournalVerbs.cs needs one more fixture step in the
      family it already hosts for exactly this reason:

        case "fade-screen":
            ScreenFader.StartFade(Color.black, ctx.Args.Int("fade_seconds", 5));
            break;

      then this envelope would exercise it end to end:

        commands/a18-21.json
        {"id":"a18-21","op":"journal-selftest","args":{"steps":["fade-screen"],"fade_seconds":5}}
        commands/a18-21b.json
        {"id":"a18-21b","op":"advance","args":{"ticks":600}}
        expected: results/a18-21b.json -> {"ok":false,"error":{"code":"cannot-set-speed",...}}

      That file is outside spec 1.8's assigned set, so the step is left here
      wired and skipped rather than added silently.
EOF
hr

# --- 22  zero red errors across the whole run -------------------------------
echo "22  zero red errors across the whole run (the standing invariant)"
send a18-22 journal "{\"types\":[\"red_error\"],\"since_seq\":${START_SEQ%.*},\"limit\":500}" 60
eq  "no red_error journal lines since step 01" '.data.count' 0
if [ "$(q '.data.count')" != "0" ]; then
  note "$(q '.data.events | tostring')"
fi
send a18-22b status '{}' 30 || true
eq  "the bench ends paused"          '.data.paused' true
eq  "at speed Paused"                '.data.speed'  "Paused"
hr

echo "PASS $PASS   FAIL $FAIL   SKIP $SKIP"
if [ "$FAIL" -gt 0 ]; then
  echo "failed steps:$FAILED_STEPS"
  exit 1
fi
echo "spec 1.8 acceptance: GREEN (see the SKIP lines for what was not demonstrated)"
exit 0
