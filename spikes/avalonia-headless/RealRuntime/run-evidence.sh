#!/usr/bin/env bash
# Real-runtime evidence harness for spikes/avalonia-headless.
# Host assumptions: Hyprland 0.56 with Lua dispatchers, at-spi2-core, grim, xdotool.
# Usage: run-evidence.sh wayland|x11  <evidence-dir>
# Proof artifacts go to <evidence-dir>; nothing outside it is modified.
set -u
PLATFORM=${1:?wayland|x11}
D=${2:?evidence dir}
SCRIPT_DIR=$(dirname "$(readlink -f "$0")")
ROOT=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
APP="$ROOT/spikes/avalonia-headless/RealRuntime/bin/Release/net11.0/RealRuntime.dll"
A11Y="unix:path=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/at-spi/bus_0"
PROBE="$SCRIPT_DIR/probe_atspi.py"
mkdir -p "$D"
rm -f "$D"/*

win_pid() {
  /usr/bin/python3 -c "
import json,subprocess
d=json.loads(subprocess.check_output(['hyprctl','clients','-j'],cwd='/tmp'))
print([c['pid'] for c in d if c.get('class')=='RealRuntime'][0])" 2>/dev/null
}
hypr() { ( cd /tmp && hyprctl dispatch "$1" >/dev/null 2>&1 ); }

if [ "$PLATFORM" = wayland ]; then
  LAUNCH=(env SPIKE_PLATFORM=wayland SPIKE_EXIT_AFTER_SECONDS=90 WAYLAND_DISPLAY=wayland-1 DISPLAY=:0)
else
  LAUNCH=(env -u WAYLAND_DISPLAY SPIKE_EXIT_AFTER_SECONDS=90 DISPLAY=:0)
fi

( cd /tmp && timeout 110 "${LAUNCH[@]}" dotnet "$APP" > "$D/app.log" 2>&1 ) &
SHELL_PID=$!
for _ in $(seq 1 50); do
  grep -q 'window opened' "$D/app.log" 2>/dev/null && break
  sleep 0.2
done
sleep 1
WPID=$(win_pid)
echo "platform=$PLATFORM shell_pid=$SHELL_PID win_pid=$WPID" > "$D/pids.txt"

# Float the window so the requested 640x480 client size is honoured by the tiler.
hypr "hl.dsp.focus({ window = 'pid:$WPID' })"
hypr "hl.dsp.window.float({ action = 'toggle', window = 'pid:$WPID' })"
sleep 1

/usr/bin/python3 - <<PY > "$D/window-state.json"
import json,subprocess
d=json.loads(subprocess.check_output(["hyprctl","clients","-j"],cwd="/tmp"))
c=[x for x in d if x.get("class")=="RealRuntime"][0]
json.dump({k:c.get(k) for k in ("title","class","pid","at","size","floating","mapped","visible","xwayland","workspace","address")}, open("/dev/stdout","w"), indent=2)
PY

GEOM=$(/usr/bin/python3 -c "
import json
c=json.load(open('$D/window-state.json'))
x,y=c['at']; w,h=c['size']; print(f'{x},{y} {w}x{h}')")
( cd /tmp && WAYLAND_DISPLAY=wayland-1 grim -g "$GEOM" "$D/window.png" ) 2> "$D/grim.err"
printf 'geom=%s\n' "$GEOM" > "$D/screenshot.txt"
file "$D/window.png" >> "$D/screenshot.txt" 2>&1

if [ "$PLATFORM" = x11 ]; then
  WID=$(/usr/bin/python3 -c "
import subprocess
print(subprocess.check_output(['xdotool','search','--name','Synthetic library spike'],text=True).split()[0])" 2>/dev/null)
  echo "x11_window_id=$WID" >> "$D/pids.txt"
  ( cd /tmp && xdotool getwindowgeometry --shell "$WID" > "$D/xdotool-geometry.txt" 2>&1 )
  ( cd /tmp && import -display :0 -window "$WID" "$D/window-import.png" ) 2> "$D/import.err"
  file "$D/window-import.png" >> "$D/screenshot.txt" 2>&1
fi

# --- AT-SPI screen-reader path ---
( cd /tmp && timeout 40 /usr/bin/python3 "$PROBE" --pid "$WPID" --json "$D/atspi-tree.json" > "$D/atspi-probe.log" 2>&1 )
echo "atspi_probe_exit=$?" >> "$D/atspi-summary.txt"
grep -v -E 'DeprecationWarning|^  ' "$D/atspi-probe.log" >> "$D/atspi-summary.txt"
( cd /tmp && gdbus call --address "$A11Y" --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.ListNames > "$D/a11y-bus-names.txt" 2>&1 )
/usr/bin/python3 - <<PY >> "$D/atspi-summary.txt"
import re,subprocess
txt=open("$D/a11y-bus-names.txt").read()
pids=set()
for n in re.findall(r"'(:[0-9.]+)'", txt):
    try:
        out=subprocess.check_output(["gdbus","call","--address","$A11Y","--dest","org.freedesktop.DBus","--object-path","/org/freedesktop/DBus","--method","org.freedesktop.DBus.GetConnectionUnixProcessID",n],cwd="/tmp",text=True)
        m=re.search(r"uint32 (\d+)",out)
        if m: pids.add(int(m.group(1)))
    except Exception:
        pass
print("a11y_bus_client_pids", sorted(pids))
print("app_pid_on_a11y_bus", $WPID in pids or $SHELL_PID in pids)
PY
cat "$D/atspi-summary.txt"
if [ "$PLATFORM" = wayland ]; then
  send() { hypr "hl.dsp.send_shortcut({ mods = '', key = '$1', window = 'pid:$WPID' })"; sleep 0.5; }
  : > "$D/keyboard.txt"
  echo "start: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  send TAB;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  send SPACE; echo "after SPACE: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  send TAB;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  send TAB;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  send SPACE; echo "after SPACE: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
else
  key() {
    hypr "hl.dsp.focus({ window = 'pid:$WPID' })"
    sleep 0.4
    ( cd /tmp && xdotool key --clearmodifiers "$1" ) >/dev/null 2>&1
    sleep 0.9
  }
  : > "$D/keyboard.txt"
  echo "start: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  key Tab;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  key space; echo "after SPACE: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  key Tab;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  key Tab;  echo "after TAB:   $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
  key space; echo "after SPACE: $(tail -1 "$D/app.log")" >> "$D/keyboard.txt"
fi
cat "$D/keyboard.txt"

# AT-SPI actions run after keyboard so traversal starts from the pristine window.
if [ "$PLATFORM" = x11 ]; then
  ( cd /tmp && timeout 40 /usr/bin/python3 "$PROBE" --pid "$WPID" --act --json "$D/atspi-actions.json" > "$D/atspi-actions.log" 2>&1 )
  echo "atspi_actions_exit=$?" >> "$D/atspi-summary.txt"
fi

# --- clean close, then confirm no leftover process ---
hypr "hl.dsp.window.close({ window = 'pid:$WPID' })"
for _ in $(seq 1 40); do
  kill -0 "$SHELL_PID" 2>/dev/null || break
  sleep 0.25
done
if kill -0 "$SHELL_PID" 2>/dev/null; then
  echo "close_timeout_sigterm=yes" >> "$D/pids.txt"
  kill -TERM "$SHELL_PID" 2>/dev/null
fi
wait "$SHELL_PID" 2>/dev/null
echo "exit_code=$?" >> "$D/pids.txt"
sleep 0.5
if pgrep -f 'dotnet .*bin/Release/net11\.0/RealRuntime\.dll' >/dev/null 2>&1; then
  echo "LEFTOVER_PROCESS yes" >> "$D/pids.txt"
else
  echo "LEFTOVER_PROCESS no" >> "$D/pids.txt"
fi
cat "$D/pids.txt"
