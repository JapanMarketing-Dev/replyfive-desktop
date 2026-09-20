#!/usr/bin/env bash
# コンテナ内で動く結合テスト。サーバ（memory / mock）もコンテナ内で動かす（端末は http をローカルホストにしか許さないため）。
set -uo pipefail
export DISPLAY=:99
export HOME=/root
export XDG_RUNTIME_DIR=/tmp/xdg-run; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
export GTK_MODULES=gail:atk-bridge
export NO_AT_BRIDGE=0
APP=/work/app/ReplyFive
LOG=/root/.local/state/replyfive/app.log
fail() { echo "FAIL: $*"; echo "---- app.log"; grep -v 'atspi connect failed' "$LOG" 2>/dev/null | tail -40; echo "---- stdout/stderr"; tail -20 /tmp/replyfive.out 2>/dev/null; echo "---- entry.txt"; cat /tmp/entry.txt 2>/dev/null; echo; exit 1; }

Xvfb :99 -screen 0 1280x800x24 >/tmp/xvfb.log 2>&1 &
sleep 1
eval "$(dbus-launch --sh-syntax)"
export DBUS_SESSION_BUS_ADDRESS
/usr/libexec/at-spi-bus-launcher --launch-immediately >/tmp/atspi.log 2>&1 &
sleep 1
/usr/libexec/at-spi2-registryd >/tmp/registryd.log 2>&1 &
sleep 1
# Secret Service（gnome-keyring、空パスワードで解錠）。端末トークンと鍵が libsecret 経由で入ることを確かめる
# 既定コレクション（login）をプロンプト無しで用意する（CI 向けの平文鍵束。実機では利用者の鍵束が使われる）
mkdir -p "$HOME/.local/share/keyrings"
printf '[keyring]\ndisplay-name=Login\nctime=0\nmtime=0\nlock-on-idle=false\nlock-after=false\n' > "$HOME/.local/share/keyrings/login.keyring"
printf 'login' > "$HOME/.local/share/keyrings/default"
eval "$(printf '' | gnome-keyring-daemon --unlock --replace --components=secrets --daemonize 2>/dev/null | grep '=')" || true
export GNOME_KEYRING_CONTROL
sleep 1
# デスクトップのアクセシビリティを有効化（アプリの「有効にする…」と同じ操作）
dbus-send --session --dest=org.a11y.Bus --type=method_call /org/a11y/bus org.freedesktop.DBus.Properties.Set string:org.a11y.Status string:IsEnabled variant:boolean:true || true

python3 /work/chat_app.py >/tmp/chat.log 2>&1 &
sleep 3
WIN=$(xdotool search --name "Slack" | head -1); xdotool windowfocus --sync "$WIN" 2>/dev/null || true

# サーバ（memory / mock）を起動し、接続リンクを発行
mkdir -p /srv /tmp/upd
cat > /srv/ReplyFive-0.8.1.AppImage <<'FAKE'
#!/bin/sh
# 更新テスト用の「新しい版」。--version で新しい番号を返し、起動されたら印を残す
[ "$1" = "--version" ] && { echo 0.8.1; exit 0; }
echo relaunched > /tmp/relaunched; sleep 30
FAKE
chmod +x /srv/ReplyFive-0.8.1.AppImage
openssl req -x509 -newkey rsa:2048 -nodes -keyout /tmp/k.pem -out /tmp/c.pem -subj /CN=localhost -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -days 2 >/dev/null 2>&1
cp /tmp/c.pem /usr/local/share/ca-certificates/replyfive-test.crt && update-ca-certificates >/dev/null 2>&1
python3 - <<'PY' >/tmp/https.log 2>&1 &
import http.server, ssl, os
os.chdir('/srv')
h = http.server.HTTPServer(('127.0.0.1', 8443), http.server.SimpleHTTPRequestHandler)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain('/tmp/c.pem', '/tmp/k.pem')
h.socket = ctx.wrap_socket(h.socket, server_side=True); h.serve_forever()
PY
REPLYFIVE_STORE=memory LLM_PROVIDER=mock ADMIN_BOOTSTRAP_TOKEN=dev SESSION_SECRET=dev PORT=8787 HOST=127.0.0.1 \
  LATEST_LINUX_VERSION=0.8.1 DOWNLOAD_URL_LINUX=https://localhost:8443/ReplyFive-0.8.1.AppImage /work/server/replyfive serve >/tmp/server.log 2>&1 &
for i in $(seq 1 30); do curl -s -m 1 http://127.0.0.1:8787/v1/meta >/dev/null && break; sleep 1; done
curl -s -c /tmp/cj -H 'content-type: application/json' -d '{"token":"dev"}' http://127.0.0.1:8787/auth/bootstrap >/dev/null
LINK=$(curl -s -b /tmp/cj -X POST http://127.0.0.1:8787/v1/admin/devices/link | python3 -c "import json,sys; print(json.load(sys.stdin)['connect_url'])")
echo "link: ${LINK:0:60}…"

# ReplyFive を起動
"$APP" >/tmp/replyfive.out 2>&1 &
sleep 6
"$APP" --send cmd:hide
"$APP" --send "cmd:server http://127.0.0.1:8787"; sleep 1
"$APP" --send "$LINK"; sleep 4
"$APP" --send cmd:state; sleep 1
grep -q 'connect ok' "$LOG" || fail "connect"
grep -q 'launch distribution=direct' "$LOG" || fail "distribution detection (expected direct)"
[ -f "$HOME/.local/share/applications/app.replyfive.ReplyFive.desktop" ] || fail "desktop file not registered"
[ -f "$HOME/.local/share/icons/hicolor/256x256/apps/app.replyfive.ReplyFive.png" ] || fail "icon not installed"
TOKEN_IN_KEYRING=$(secret-tool lookup name device-token 2>/dev/null | wc -c)
[ "$TOKEN_IN_KEYRING" -gt 10 ] || fail "device token not stored via Secret Service (libsecret)"
echo "secret service: device-token stored ($TOKEN_IN_KEYRING bytes)"

# 会話アプリを前面にしてショートカット（X11 grab 経由）でパネルを開く
xdotool windowfocus --sync "$WIN"; sleep 1
echo "x focus: $(xdotool getwindowfocus getwindowname 2>&1) (chat window $WIN)"
xdotool key ctrl+shift+r; sleep 3
grep -q 'panel shown' "$LOG" || fail "hotkey did not open the panel"
CAP=$(grep 'capture app=' "$LOG" | tail -1); echo "$CAP"
echo "$CAP" | grep -q 'source=Window' || fail "conversation not read via AT-SPI"
echo "$CAP" | grep -q 'platform=Slack' || fail "platform detection"
echo "$CAP" | grep -q 'contact=True' || fail "contact not detected"

# 生成 → 差し込み（AT-SPI EditableText）
"$APP" --send "cmd:intent 了解、明日送ります"; sleep 1
"$APP" --send cmd:generate; sleep 5
"$APP" --send cmd:state; sleep 1
grep -q 'phase=Ready' "$LOG" || fail "generation"
import -display :99 -window root /work/shots/linux-1.png 2>/dev/null || true
"$APP" --send cmd:insert; sleep 4
import -display :99 -window root /work/shots/linux-2.png 2>/dev/null || true
grep 'insert result' "$LOG" | tail -1
grep -q 'insert result ok=True' "$LOG" || fail "insert"
grep -q '明日送ります' /tmp/entry.txt || fail "entry text: $(cat /tmp/entry.txt 2>/dev/null)"
echo "entry: $(cat /tmp/entry.txt)"

# 初回設定「会話を見せてください」（付録CF-6）：収集した会話が候補として並ぶか
"$APP" --send "cmd:onboarding-page 2"; sleep 3
import -display :99 -window root /work/shots/linux-onboarding.png 2>/dev/null || true
grep 'onboarding show candidates=' "$LOG" | tail -1
grep -q 'onboarding show candidates=[1-9]' "$LOG" || fail "onboarding candidates not listed"
"$APP" --send cmd:hide
# 会話の記録（背景収集）が溜まっているか
sleep 3
"$APP" --send cmd:state
"$APP" --send cmd:quit; sleep 2
echo "---- conversation ingest"; grep 'conversation ingest' "$LOG" | tail -3

# AppImage の自己更新（付録CD の Linux 版）：書込み可の場所に置いた AppImage で起動 → 新版を取得・検証 → 差し替え → 再起動
if [ -f /work/dist/ReplyFive.AppImage ]; then
  cp /work/dist/ReplyFive.AppImage /tmp/upd/ReplyFive.AppImage
  APPIMAGE_EXTRACT_AND_RUN=1 /tmp/upd/ReplyFive.AppImage >/tmp/appimage.out 2>&1 &
  sleep 10
  "$APP" --send cmd:hide
  PID=$(pgrep -f 'ReplyFive' | head -1); echo "appimage env: $(tr '\0' '\n' < /proc/$PID/environ 2>/dev/null | grep -E '^APPIMAGE=|^APPDIR=' | tr '\n' ' ')"
  echo "server meta: $(curl -s -H 'x-replyfive-client: linux/0.8.0' http://127.0.0.1:8787/v1/meta | head -c 400)"
  "$APP" --send cmd:update-check
  for i in $(seq 1 60); do grep -q 'update ready version=0.8.1' "$LOG" && break; sleep 1; done
  grep -q 'update ready version=0.8.1' "$LOG" || fail "update not prepared: $(grep 'update' "$LOG" | tail -3)"
  "$APP" --send cmd:update-install
  for i in $(seq 1 30); do [ -f /tmp/relaunched ] && break; sleep 1; done
  [ -f /tmp/relaunched ] || fail "relaunch after update did not happen: $(grep 'update' "$LOG" | tail -3)"
  [ "$(/tmp/upd/ReplyFive.AppImage --version)" = "0.8.1" ] || fail "AppImage was not replaced"
  echo "appimage self-update: ok ($(grep -c 'update' "$LOG") log lines)"
else
  echo "appimage self-update: skipped (dist/ReplyFive.AppImage not mounted)"
fi
echo "PASS"
