#!/usr/bin/env bash
# コンテナ内：D-Bus / AT-SPI → cage headless（GTK 会話アプリを主アプリとして） → GTK 会話アプリ（Wayland）→ サーバ → ReplyFive（Wayland）→ 読み取り・生成・差し込み
set -uo pipefail
export HOME=/root
export XDG_RUNTIME_DIR=/tmp/xdg-run; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
export GTK_MODULES=gail:atk-bridge NO_AT_BRIDGE=0
APP=/work/app/ReplyFive
LOG=/root/.local/state/replyfive/app.log
fail() { echo "FAIL: $*"; echo "---- app.log"; grep -v 'atspi connect failed' "$LOG" 2>/dev/null | tail -40; echo "---- stdout/stderr"; tail -20 /tmp/replyfive.out 2>/dev/null; echo "---- cage"; tail -5 /tmp/cage.log; exit 1; }

export DBUS_SESSION_BUS_ADDRESS=$(dbus-daemon --session --fork --print-address --address=unix:path=$XDG_RUNTIME_DIR/bus)
/usr/libexec/at-spi-bus-launcher --launch-immediately >/tmp/atspi.log 2>&1 & sleep 1
/usr/libexec/at-spi2-registryd >/tmp/registryd.log 2>&1 & sleep 1
dbus-send --session --dest=org.a11y.Bus --type=method_call /org/a11y/bus org.freedesktop.DBus.Properties.Set string:org.a11y.Status string:IsEnabled variant:boolean:true || true

# cage（headless、pixman）。主アプリとして GTK3 の会話アプリ（Wayland ネイティブ）を起動する。
WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 GDK_BACKEND=wayland cage python3 /work/chat_app.py >/tmp/cage.log 2>&1 &
CAGE_PID=$!
for i in $(seq 1 20); do ls "$XDG_RUNTIME_DIR"/wayland-[0-9] >/dev/null 2>&1 && break; sleep 0.5; done
export WAYLAND_DISPLAY=$(ls "$XDG_RUNTIME_DIR" | grep -E '^wayland-[0-9]$' | head -1)
[ -n "$WAYLAND_DISPLAY" ] && kill -0 $CAGE_PID 2>/dev/null || fail "cage did not start"
export XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=cage
unset DISPLAY
# 仮想キーボードを 1 台つないでおく（キーボードの無い seat では wl_keyboard.enter が配られず、GTK の FOCUSED 状態が立たない）
wtype -s 900000 -k Escape >/dev/null 2>&1 &
sleep 3
echo "wayland display: $WAYLAND_DISPLAY  xdg_wm_base: $(wayland-info 2>/dev/null | grep -o "xdg_wm_base.*version: *[0-9]*" | head -1)  seat: $(wayland-info 2>/dev/null | grep -A2 "'wl_seat'" | grep -o 'capabilities:.*' | head -1)"

REPLYFIVE_STORE=memory LLM_PROVIDER=mock ADMIN_BOOTSTRAP_TOKEN=dev SESSION_SECRET=dev PORT=8787 HOST=127.0.0.1 /work/server/replyfive serve >/tmp/server.log 2>&1 &
for i in $(seq 1 30); do curl -s -m 1 http://127.0.0.1:8787/v1/meta >/dev/null && break; sleep 1; done
curl -s -c /tmp/cj -H 'content-type: application/json' -d '{"token":"dev"}' http://127.0.0.1:8787/auth/bootstrap >/dev/null
LINK=$(curl -s -b /tmp/cj -X POST http://127.0.0.1:8787/v1/admin/devices/link | python3 -c "import json,sys; print(json.load(sys.stdin)['connect_url'])")

REPLYFIVE_FORCE_WAYLAND=1 "$APP" >/tmp/replyfive.out 2>&1 &
sleep 8
grep -q 'launch' "$LOG" || fail "app did not start under Wayland"
"$APP" --send cmd:hide
"$APP" --send "cmd:server http://127.0.0.1:8787"; sleep 1
"$APP" --send "$LINK"; sleep 4
grep -q 'connect ok' "$LOG" || fail "connect"

# 会話アプリ（Wayland ネイティブ）が焦点のまま、XWayland 上の X11 grab でショートカットを送る
grep -q 'panel shown' "$LOG" || { echo "opening the panel via --send (no Portal backend here)"; "$APP" --send cmd:toggle; sleep 3; }
grep -q 'panel shown' "$LOG" || fail "panel did not open"
CAP=$(grep 'capture app=' "$LOG" | tail -1); echo "$CAP"
echo "$CAP" | grep -q 'source=Window' || fail "conversation not read via AT-SPI (Wayland)"
echo "$CAP" | grep -q 'platform=Slack' || fail "platform detection"
"$APP" --send "cmd:intent 了解、明日送ります"; sleep 1
"$APP" --send cmd:generate; sleep 5
"$APP" --send cmd:state; sleep 1
grep -q 'phase=Ready' "$LOG" || fail "generation"
"$APP" --send cmd:insert; sleep 4
grep -q 'insert result ok=True' "$LOG" || fail "insert (Wayland)"
grep -q '明日送ります' /tmp/entry.txt || fail "entry text: $(cat /tmp/entry.txt 2>/dev/null)"
echo "entry: $(cat /tmp/entry.txt)"
echo "hotkey lines: $(grep -E 'hotkey' "$LOG" | tr '\n' ' ')"
echo "backend: $(grep -E 'launch' "$LOG" | tail -1)"
"$APP" --send cmd:quit; sleep 1
echo "PASS"
