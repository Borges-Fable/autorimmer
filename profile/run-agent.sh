#!/usr/bin/env bash
# Launch the _RimWorld-Agent bench profile with isolated saves/config.
# Spec 0.1 (git-bug 3fa4cf5). Isolation pattern: _RimWorld-Testing/run-testing.sh.
#
# This launcher is COPIED into the _RimWorld-Agent profile by
# make-profile-agent.sh and must be run from there. It refuses to launch any
# other install: the agent bench is the single carve-out to the workspace
# "never launch RimWorld" rule.
#
# Flags (everything else is passed through to RimWorldLinux):
#   --fps N       mangohud fps cap while unwatched (default 30). The cap lives
#                 in config/mangohud-agent.conf, which MangoHud watches for
#                 changes: rewrite fps_limit there to re-cap a RUNNING game
#                 (rwa watch raises it, spec 1.4).
#   --quicktest   boot straight into a generated 250x250 test map
#                 (vanilla -quicktest: Root_Play.SetupForQuickTestPlay).
#   --xvfb        fully detached fallback: run inside Xvfb on $XVFB_DISPLAY
#                 (default :99) instead of the live session. Needs xorg-server-xvfb.
#   --vnc         with --xvfb: attach x11vnc on port $VNC_PORT (default 5900,
#                 localhost only) so the run is watchable on demand.
#   --batchmode   pass Unity -batchmode -nographics (experiment; spike 0.1
#                 verdict: FINDINGS.md).
#   --no-rule     skip the Hyprland special-workspace windowrule.
set -e
SCRIPT_PATH="$(readlink -f "$0")"
GAME_DIR="$(dirname "$SCRIPT_PATH")"

case "$GAME_DIR" in
    */_RimWorld-Agent) ;;
    *) echo "refusing: run-agent.sh must live in the _RimWorld-Agent profile (got $GAME_DIR)" >&2
       echo "the agent bench is the only install this launcher may start" >&2
       exit 1 ;;
esac
cd "$GAME_DIR"
[ -x ./RimWorldLinux ] || chmod +x ./RimWorldLinux

FPS=30
USE_XVFB=0
USE_VNC=0
BATCHMODE=0
QUICKTEST=0
APPLY_RULE=1
XVFB_DISPLAY="${XVFB_DISPLAY:-:99}"
VNC_PORT="${VNC_PORT:-5900}"
PASS=()
while [ $# -gt 0 ]; do
    case "$1" in
        --fps)       FPS="$2"; shift 2 ;;
        --quicktest) QUICKTEST=1; shift ;;
        --xvfb)      USE_XVFB=1; shift ;;
        --vnc)       USE_VNC=1; shift ;;
        --batchmode) BATCHMODE=1; shift ;;
        --no-rule)   APPLY_RULE=0; shift ;;
        *)           PASS+=("$1"); shift ;;
    esac
done

# --- isolation: self-contained config home, no Steam attach ------------------
export XDG_CONFIG_HOME="$GAME_DIR/config"
unset SteamAppId SteamGameId SteamOverlayGameId

# --- window parking: Hyprland special workspace, silent (no focus steal) -----
# Dialect verified on Hyprland 0.55.1 (this box): inline dynamic rule via
# `hyprctl keyword windowrule` with `match:class <regex>`. `silent` keeps the
# workspace from being brought forward, so focus never moves.
if [ "$USE_XVFB" -eq 0 ] && [ "$BATCHMODE" -eq 0 ] && [ "$APPLY_RULE" -eq 1 ] \
   && command -v hyprctl >/dev/null 2>&1 && [ -n "${HYPRLAND_INSTANCE_SIGNATURE:-}" ]; then
    hyprctl keyword windowrule \
        "workspace special:rwagent silent, match:class ^(RimWorldLinux)$" >/dev/null \
        && echo "windowrule: RimWorldLinux -> special:rwagent (silent)" \
        || echo "WARN: hyprctl windowrule failed; window will open normally" >&2
fi

# --- fps cap: mangohud, live-editable config file ----------------------------
# MangoHud watches its config file; rewriting fps_limit re-caps the running
# game without a restart (this is the `rwa watch` mechanism, spec 1.4).
MANGO_CONF="$GAME_DIR/config/mangohud-agent.conf"
mkdir -p "$GAME_DIR/config"
printf 'fps_limit=%s\nno_display=1\n' "$FPS" > "$MANGO_CONF"
export MANGOHUD_CONFIGFILE="$MANGO_CONF"

GAME_ARGS=()
[ "$QUICKTEST" -eq 1 ] && GAME_ARGS+=("-quicktest")
[ "$BATCHMODE" -eq 1 ] && GAME_ARGS+=("-batchmode" "-nographics")
GAME_ARGS+=("${PASS[@]}")

run_game() {
    if [ "$BATCHMODE" -eq 1 ] || ! command -v mangohud >/dev/null 2>&1; then
        # -nographics renders nothing; mangohud's cap is meaningless there.
        LC_ALL=C ./RimWorldLinux "${GAME_ARGS[@]}"
    else
        LC_ALL=C mangohud ./RimWorldLinux "${GAME_ARGS[@]}"
    fi
}

if [ "$USE_XVFB" -eq 1 ]; then
    command -v Xvfb >/dev/null 2>&1 || {
        echo "Xvfb not installed (pacman -S xorg-server-xvfb). Aborting." >&2; exit 1; }
    Xvfb "$XVFB_DISPLAY" -screen 0 1280x768x24 &
    XVFB_PID=$!
    trap '[ -n "${VNC_PID:-}" ] && kill "$VNC_PID" 2>/dev/null; kill "$XVFB_PID" 2>/dev/null' EXIT
    sleep 1
    if [ "$USE_VNC" -eq 1 ]; then
        command -v x11vnc >/dev/null 2>&1 || {
            echo "x11vnc not installed (pacman -S x11vnc). Aborting." >&2; exit 1; }
        x11vnc -display "$XVFB_DISPLAY" -localhost -rfbport "$VNC_PORT" -forever -shared -quiet &
        VNC_PID=$!
        echo "x11vnc on localhost:$VNC_PORT"
    fi
    # Point the game at the virtual display; drop Wayland so SDL/Unity picks X11.
    DISPLAY="$XVFB_DISPLAY" WAYLAND_DISPLAY= run_game
else
    run_game
fi
