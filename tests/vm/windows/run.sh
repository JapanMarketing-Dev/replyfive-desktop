#!/usr/bin/env bash
# Windows 11 ARM64 の検証 VM（UTM、create-vm.sh）で Windows クライアントの結合テストを流す（付録CH-2）。
# Linux の tests/linux-integration/inside.sh と同じ流れ：接続 → ショートカット → UIA で会話を読む → 生成（mock）→ 差し込み → 初回設定の候補。
# 前提: VM が起動して sshd が上がっている（ssh -p 2223 rf@127.0.0.1）。
#       APP_ZIP（dotnet publish の win-arm64 を zip）、SERVER_EXE（GOOS=windows GOARCH=arm64 の replyfive.exe）。
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APP_ZIP="${APP_ZIP:-/tmp/claude/rfwin/app-win-arm64.zip}"
SERVER_EXE="${SERVER_EXE:-/tmp/claude/rfwin/replyfive-server.exe}"
OUT="${OUT:-$HERE/../../../dist/shots}"; mkdir -p "$OUT"
RF="${RF_EXE:-C:\\rf\\app\\win-arm64\\ReplyFive.exe}"   # MSIX で試すときは RF_EXE=replyfive.exe（実行エイリアス）
SSHOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -p 2223)
# ssh 越しの PowerShell は既定の OEM コードページで書き出すので日本語が ? になる。毎回 UTF-8 に切り替える
S() { ssh "${SSHOPT[@]}" rf@127.0.0.1 "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $*"; }
SCPOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -P 2223)
SCP() { scp "${SCPOPT[@]}" "$@"; }
fail() { echo "FAIL: $*"; S 'Get-Content $env:LOCALAPPDATA\ReplyFive\logs\app.log -Tail 40' 2>/dev/null; exit 1; }
# 対話セッションで実行（agent.ps1 経由）。$1 = PowerShell の本文、標準出力を返す
AGENT_N=0
A() {
  AGENT_N=$((AGENT_N+1)); local id; id=$(printf 'c%04d' "$AGENT_N")
  printf '%s\n' "$1" | S "Set-Content -Path C:\\rf\\cmd\\$id.ps1.tmp -Encoding utf8 -Value ([Console]::In.ReadToEnd()); Move-Item C:\\rf\\cmd\\$id.ps1.tmp C:\\rf\\cmd\\$id.ps1 -Force"
  for i in $(seq 1 120); do
    if S "Test-Path C:\\rf\\cmd\\$id.out" | grep -q True; then S "Get-Content C:\\rf\\cmd\\$id.out -Raw"; S "Remove-Item C:\\rf\\cmd\\$id.out -Force"; return 0; fi
    sleep 1
  done
  echo "agent timeout: $id"; return 1
}

echo "== ssh"
S 'hostname; (Get-CimInstance Win32_OperatingSystem).Caption + " " + (Get-CimInstance Win32_OperatingSystem).Version; $env:PROCESSOR_ARCHITECTURE' || fail "ssh"

echo "== stop previous"
S 'schtasks /end /tn rf-agent 2>$null | Out-Null; Get-Process ReplyFive,replyfive-server,msedge,cmd -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Get-Process powershell -ErrorAction SilentlyContinue | Where-Object SessionId -ne 0 | Stop-Process -Force -ErrorAction SilentlyContinue; Remove-Item C:\rf\cmd\* -Force -ErrorAction SilentlyContinue; schtasks /delete /tn rf-agent /f 2>$null | Out-Null'
echo "== copy"
if [ -z "${SKIP_COPY:-}" ]; then
S 'New-Item -ItemType Directory -Path C:\rf, C:\rf\cmd, C:\rf\app.slack.com\client, C:\rf\shots -Force | Out-Null'
SCP "$APP_ZIP" rf@127.0.0.1:C:/rf/app.zip
SCP "$SERVER_EXE" rf@127.0.0.1:C:/rf/replyfive-server.exe
SCP "$HERE/assets/general.html" rf@127.0.0.1:C:/rf/app.slack.com/client/general.html
S 'Remove-Item C:\rf\app -Recurse -Force -ErrorAction SilentlyContinue; Expand-Archive -Path C:\rf\app.zip -DestinationPath C:\rf\app -Force; Test-Path C:\rf\app\win-arm64\ReplyFive.exe'
fi

SCP "$HERE/assets/agent.ps1" rf@127.0.0.1:C:/rf/agent.ps1
SCP -r "$HERE/assets/steps" rf@127.0.0.1:C:/rf/
echo "== agent (interactive session)"
S 'schtasks /create /tn rf-agent /tr "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\rf\agent.ps1" /sc once /st 00:00 /it /rl limited /f | Out-Null; schtasks /run /tn rf-agent | Out-Null; Start-Sleep 2; Get-Content C:\rf\agent.log -Tail 1'
A 'whoami; [System.Diagnostics.Process]::GetCurrentProcess().SessionId' || fail "agent"

echo "== server (memory / mock)"
A '& C:\rf\steps\start-server.ps1'
for i in $(seq 1 30); do S 'try { (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:8787/v1/meta -TimeoutSec 2).StatusCode } catch { 0 }' | grep -q 200 && break; sleep 1; done
LINK=$(S 'Set-Content -Path C:\rf\bootstrap.json -Value "{`"token`":`"dev`"}" -Encoding ascii; curl.exe -s -c C:\rf\cj -H "content-type: application/json" -d "@C:\rf\bootstrap.json" http://127.0.0.1:8787/auth/bootstrap | Out-Null; curl.exe -s -b C:\rf\cj -X POST http://127.0.0.1:8787/v1/admin/devices/link' | python3 -c "import json,sys; print(json.load(sys.stdin)['connect_url'])")
echo "link: ${LINK:0:60}…"

echo "== chat page (Edge) and ReplyFive"
EDGE_MSI="${EDGE_MSI:-$HOME/.cache/utm/win/MicrosoftEdgeEnterpriseARM64.msi}"
if ! S 'Test-Path "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"' | grep -q True; then
  echo "== install Edge (UUP dump の ISO には Edge が入っていない)"
  [ -f "$EDGE_MSI" ] || fail "Edge の MSI が無い: $EDGE_MSI（edgeupdates.microsoft.com/api/products?view=enterprise の Windows/arm64）"
  SCP "$EDGE_MSI" rf@127.0.0.1:C:/rf/edge.msi
  S 'Start-Process msiexec.exe -ArgumentList "/i","C:\rf\edge.msi","/qn","/norestart" -Wait; Test-Path "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"' | grep -q True || fail "Edge install"
fi
A '& C:\rf\steps\start-edge.ps1'
A "& C:\\rf\\steps\\start-app.ps1 '$RF'"
S "& '$RF' --send cmd:hide; & '$RF' --send 'cmd:server http://127.0.0.1:8787'; Start-Sleep 1; & '$RF' --send '$LINK'; Start-Sleep 4; & '$RF' --send cmd:state; Start-Sleep 1"
LOG='Get-Content $env:LOCALAPPDATA\ReplyFive\logs\app.log'
S "$LOG | Select-String 'connect ok' | Select-Object -Last 1" | grep -q 'connect ok' || fail "connect"
S "$LOG | Select-String 'launch distribution=' | Select-Object -Last 1"

echo "== hotkey (Ctrl+Shift+R) with Edge in front"
# 前面化と Ctrl+Shift+R は QMP（本物のマウス・キーボード入力。ゲスト内の SetForegroundWindow は背景プロセスからだと効かない）
QMP="$HERE/assets/qmp.py"
S "& '$RF' --send cmd:hide"; sleep 1   # 初回設定のウインドウが手前に残っていると焦点を持ち、返信欄が焦点要素にならない
"$QMP" click 600 640; sleep 1         # Edge の返信欄（前面化＋焦点。タブバーを押すと焦点がタブへ移り、焦点要素が取れない）。QMP の send-key はキーの離しを取りこぼすので、ショートカット自体は SendKeys（hotkey.ps1）
A '& C:\rf\steps\clear-entry.ps1'
A '& C:\rf\steps\hotkey.ps1'
S "$LOG | Select-String 'panel shown' | Select-Object -Last 1" | grep -q 'panel shown' || fail "hotkey did not open the panel"
S "$LOG | Select-String 'pane app=' | Select-Object -Last 1"
CAP=$(S "$LOG | Select-String 'capture app=' | Select-Object -Last 1"); echo "$CAP"
echo "$CAP" | grep -q 'platform=Slack' || fail "platform detection"

echo "== generate → insert"
S "& '$RF' --send 'cmd:intent 了解、明日送ります'; Start-Sleep 1; & '$RF' --send cmd:generate; Start-Sleep 6; & '$RF' --send cmd:state; Start-Sleep 1"
S "$LOG | Select-String 'state phase=' | Select-Object -Last 1" | grep -q 'phase=Ready' || fail "generation"
A '& C:\rf\steps\shot.ps1 C:\rf\shots\win-1.png'
S "& '$RF' --send cmd:insert; Start-Sleep 4"
S "$LOG | Select-String 'insert result' | Select-Object -Last 1" | grep -q 'ok=True' || fail "insert"
S "$LOG | Select-String 'insert trusted=' | Select-Object -Last 1"
ENTRY=$(A '& C:\rf\steps\read-entry.ps1' | tr -d '\r' | grep '^entry:'); echo "$ENTRY"
[ "$ENTRY" = "entry: 了解、明日送ります。" ] || fail "entry text（差し込みが書きかけを壊した／二重になった）"
A '& C:\rf\steps\shot.ps1 C:\rf\shots\win-2.png'
SCP rf@127.0.0.1:C:/rf/shots/win-1.png rf@127.0.0.1:C:/rf/shots/win-2.png "$OUT/" 2>/dev/null || true
echo "== windows checks"
A '& C:\rf\steps\checks.ps1'
echo "== done"
