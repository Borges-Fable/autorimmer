#!/usr/bin/env bash
# Put the running agent bench on workspace 3 and take the user there.
#
# The launcher (`run-agent.sh`) parks RimWorld on the hidden special workspace
# `special:rwagent` so an unattended run does not steal the screen. That is the
# right default for a headless acceptance run and the wrong one whenever a human
# wants to watch. This is the "let me see it" switch.
#
# Dorian's standing preference (2026-08-31): if the game is open at all, it goes
# on workspace 3 AND the focus follows it. Run this after every launch you
# intend to be watched — `run-agent.sh --no-rule` skips the special-workspace
# rule but does NOT focus anything, so this is still the script that moves you.
#
#   profile/show-bench.sh              # move it and follow
#   profile/show-bench.sh --wait 120   # wait up to 120s for the window first
#   profile/show-bench.sh --ws 4       # somewhere other than 3
#   profile/show-bench.sh --no-follow  # move the window, stay where you are
#
# Exit 0 = moved (and followed). 1 = no bench window found.
set -u

WS=3
WAIT=0
FOLLOW=1
while [ $# -gt 0 ]; do
    case "$1" in
        --ws)        WS="$2"; shift 2 ;;
        --wait)      WAIT="${2:-60}"; shift 2 ;;
        --no-follow) FOLLOW=0; shift ;;
        -h|--help)   sed -n '2,20p' "$0"; exit 0 ;;
        *)           echo "unknown flag: $1" >&2; exit 2 ;;
    esac
done

command -v hyprctl >/dev/null 2>&1 || { echo "show-bench: no hyprctl — not a Hyprland session" >&2; exit 1; }

# The class is stable across the launcher's modes; the title is not (Unity
# rewrites it during load), so match on class only.
CLASS=RimWorldLinux
found=0
deadline=$(( $(date +%s) + WAIT ))
while :; do
    if hyprctl clients -j 2>/dev/null | grep -q "\"class\": \"$CLASS\""; then found=1; break; fi
    [ "$(date +%s)" -ge "$deadline" ] && break
    sleep 2
done

if [ "$found" -ne 1 ]; then
    echo "show-bench: no $CLASS window (bench not running?)" >&2
    exit 1
fi

# movetoworkspacesilent does not drag focus with it; the explicit `workspace`
# dispatch below is what actually moves the human. Doing it in this order means
# the window is already there when the view switches, so there is no flicker of
# an empty workspace.
hyprctl dispatch movetoworkspacesilent "$WS,class:$CLASS" >/dev/null
[ "$FOLLOW" -eq 1 ] && hyprctl dispatch workspace "$WS" >/dev/null

hyprctl clients -j 2>/dev/null | python3 -c "
import json,sys
for w in json.load(sys.stdin):
    if w.get('class') == '$CLASS':
        ws = w.get('workspace', {}).get('name')
        print('bench on workspace %s, %sx%s%s' % (
            ws, w.get('size',[0,0])[0], w.get('size',[0,0])[1],
            ' (fullscreen)' if w.get('fullscreen') else ''))
" 2>/dev/null || echo "bench moved to workspace $WS"
