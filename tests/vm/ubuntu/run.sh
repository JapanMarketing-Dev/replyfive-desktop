#!/usr/bin/env bash
# Ubuntu 24.04（GNOME、実機相当）の検証 VM で Linux クライアントの GUI 結合テストを流す（付録CL-2）。
# tests/linux-integration（Xvfb のコンテナ、ウインドウマネージャ無し）で確かめられなかったところ＝
# 実際の GNOME セッション（Wayland / X11）・Portal の GlobalShortcuts・gnome-keyring の Secret Service・
# トレイ（AppIndicator）・実機の AT-SPI を確かめる。
# 前提: create-vm.sh → detach-iso.sh で VM が動き、ssh -p 2222 rf@127.0.0.1 が通る。
#       APP_TGZ（dotnet publish -r linux-arm64 を tar.gz）、SERVER_BIN（GOOS=linux GOARCH=arm64）。
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APP_TGZ="${APP_TGZ:-/tmp/claude/rflin/app-linux-arm64.tar.gz}"
SERVER_BIN="${SERVER_BIN:-/tmp/claude/rflin/replyfive-server}"
CHAT="${CHAT:-$HERE/../../linux-integration/chat_app.py}"
OUT="${OUT:-$HERE/../../../dist/shots}"; mkdir -p "$OUT"
SSHOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -p 2222)
SCPOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -P 2222)
QMP="$HERE/../windows/assets/qmp.py"; export QMP_PORT=4446 VM_NAME="ReplyFive Linux"
S() { ssh "${SSHOPT[@]}" rf@127.0.0.1 "$@"; }
# ログイン中の GNOME セッション向けに環境を揃えて実行する（ssh には DISPLAY も D-Bus も無い）
G() { S "export \$(systemctl --user show-environment | grep -E '^(DISPLAY|WAYLAND_DISPLAY|XAUTHORITY|DBUS_SESSION_BUS_ADDRESS|XDG_RUNTIME_DIR|XDG_SESSION_TYPE)=' | xargs -d'\n'); $*"; }
APP=/home/rf/rf/app/ReplyFive
LOG='/home/rf/.local/state/replyfive/app.log'
fail() { echo "FAIL: $*"; S "tail -40 $LOG" 2>/dev/null; exit 1; }

echo "== ssh"
S 'hostname; . /etc/os-release && echo "$PRETTY_NAME $(uname -m)"; echo "session: $(loginctl show-session $(loginctl list-sessions --no-legend | awk "{print \$1}" | head -1) -p Type --value 2>/dev/null)"' || fail "ssh"
G 'echo "display=$DISPLAY wayland=$WAYLAND_DISPLAY type=$XDG_SESSION_TYPE"' || fail "session env"

echo "== stop previous"
G 'pkill -f ReplyFive; pkill -f replyfive-server; pkill -f chat_app.py; sleep 1; true'

echo "== copy"
if [ -z "${SKIP_COPY:-}" ]; then
  S 'mkdir -p ~/rf/app ~/rf/shots'
  scp "${SCPOPT[@]}" "$APP_TGZ" rf@127.0.0.1:/home/rf/rf/app.tar.gz || fail "scp app"
  scp "${SCPOPT[@]}" "$SERVER_BIN" rf@127.0.0.1:/home/rf/rf/replyfive-server || fail "scp server"
  scp "${SCPOPT[@]}" "$CHAT" rf@127.0.0.1:/home/rf/rf/chat_app.py || fail "scp chat"
  S 'rm -rf ~/rf/app && mkdir -p ~/rf/app && tar xzf ~/rf/app.tar.gz -C ~/rf/app --strip-components=1 && chmod +x ~/rf/app/ReplyFive ~/rf/replyfive-server && ~/rf/app/ReplyFive --version'
fi

echo "== server (memory / mock)"
S 'REPLYFIVE_STORE=memory LLM_PROVIDER=mock ADMIN_BOOTSTRAP_TOKEN=dev SESSION_SECRET=dev PORT=8787 HOST=127.0.0.1 nohup ~/rf/replyfive-server serve >/tmp/server.log 2>&1 & sleep 1; true'
for i in $(seq 1 30); do S 'curl -s -m 1 http://127.0.0.1:8787/v1/meta >/dev/null && echo ok' | grep -q ok && break; sleep 1; done
LINK=$(S 'curl -s -c /tmp/cj -H "content-type: application/json" -d "{\"token\":\"dev\"}" http://127.0.0.1:8787/auth/bootstrap >/dev/null; curl -s -b /tmp/cj -X POST http://127.0.0.1:8787/v1/admin/devices/link' | python3 -c "import json,sys; print(json.load(sys.stdin)['connect_url'])") || fail "link"
echo "link: ${LINK:0:48}…"

echo "== chat app (GTK3, AT-SPI)"
G 'gsettings set org.gnome.desktop.interface toolkit-accessibility true; nohup python3 ~/rf/chat_app.py >/tmp/chat.log 2>&1 & sleep 3; true'
G 'echo "a11y: $(gsettings get org.gnome.desktop.interface toolkit-accessibility)"'

echo "== ReplyFive"
G "nohup $APP >/tmp/replyfive.out 2>&1 & sleep 8; true"
G "$APP --send cmd:hide; $APP --send 'cmd:server http://127.0.0.1:8787'; sleep 1; $APP --send '$LINK'; sleep 4; $APP --send cmd:state; sleep 1"
S "grep -q 'connect ok' $LOG" || fail "connect"
S "grep 'launch ' $LOG | tail -1"
S "grep -E 'backend|wayland|x11' $LOG | tail -3"

echo "== hotkey (Ctrl+Shift+R)"
"$QMP" click 500 420 >/dev/null; sleep 1     # 会話アプリの返信欄（前面化＋焦点）
G "$APP --send cmd:hide"; sleep 1
S "grep -E 'hotkey ' $LOG | tail -2"
"$QMP" shot "$OUT/linux-gui-0.png" >/dev/null
G 'xdotool key ctrl+shift+r 2>/dev/null || true'; sleep 3
S "grep -q 'panel shown' $LOG" || fail "hotkey did not open the panel"
CAP=$(S "grep 'capture app=' $LOG | tail -1"); echo "$CAP"
echo "$CAP" | grep -q 'platform=Slack' || fail "platform detection"

echo "== generate → insert"
G "$APP --send 'cmd:intent 了解、明日送ります'; sleep 1; $APP --send cmd:generate; sleep 6; $APP --send cmd:state; sleep 1"
S "grep 'state phase=' $LOG | tail -1" | grep -q 'phase=Ready' || fail "generation"
"$QMP" shot "$OUT/linux-gui-1.png" >/dev/null
G "$APP --send cmd:insert"; sleep 4
S "grep 'insert result' $LOG | tail -1" | grep -q 'ok=True' || fail "insert"
S "grep 'insert trusted' $LOG | tail -1"
ENTRY=$(S 'cat /tmp/entry.txt 2>/dev/null'); echo "entry: $ENTRY"
[ "$ENTRY" = "了解、明日送ります。" ] || fail "entry text: $ENTRY"
"$QMP" shot "$OUT/linux-gui-2.png" >/dev/null

echo "== linux checks"
S 'echo "secret service: $(secret-tool lookup name device-token 2>/dev/null | wc -c) bytes"; echo "desktop file: $(ls ~/.local/share/applications/app.replyfive.ReplyFive.desktop 2>/dev/null || echo none)"; echo "icon: $(ls ~/.local/share/icons/hicolor/256x256/apps/app.replyfive.ReplyFive.png 2>/dev/null || echo none)"; echo "autostart: $(ls ~/.config/autostart/app.replyfive.ReplyFive.desktop 2>/dev/null || echo off)"; echo "url handler: $(xdg-mime query default x-scheme-handler/replyfive 2>/dev/null)"'
echo "== done"
