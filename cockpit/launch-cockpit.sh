#!/usr/bin/env bash
# Launch the cockpit in a terminal. Absolute paths throughout: a window manager
# `exec` does not inherit anyone's working directory, which is how the first
# attempt failed silently.
set -uo pipefail
REPO=/home/dorian/projects/rimworld/autorimmer
cd "$REPO" || { echo "cannot cd to $REPO"; exec bash; }
RUN="${1:-m1-20260901}"; shift || true
echo "cockpit: repo=$REPO run=$RUN term=${TERM:-unset} size=$(tput cols 2>/dev/null)x$(tput lines 2>/dev/null)"
exec python3 "$REPO/cockpit/cockpit" "$RUN" --transcripts "$REPO/transcripts" "$@"
