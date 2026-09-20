#!/usr/bin/env bash
# Microsoft Store 提出物（MSIX）が実機の Windows で通るかを検証する（付録CH-2）。UTM の Windows 11 ARM64 VM を使う。
# ストアへ出すのは署名前の .msix（ストアが署名する）ので、ここでは同じ中身を「ばら置き（loose layout）」で開発者モード登録し、
#   ・makemsix で作ったのと同じ AppxManifest.xml が Windows の配置エンジンに受理されるか
#   ・runFullTrust のアプリとして起動し、UI Automation が使えるか
#   ・パッケージ判定（Package.Current）で配布形態が store になり、アプリ内アップデートが切れるか
#   ・windows.protocol（replyfive://）と windows.startupTask が登録されるか
# を確かめる。前提: run.sh が通る状態（VM が起動して sshd が上がっている）。
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../../.." && pwd)"
PUBLISH="${PUBLISH:-/tmp/claude/rfwin/win-arm64}"
IDENTITY_NAME="${IDENTITY_NAME:-JapanMarketing.ReplyFive}"     # 仮。Partner Center 発行の値に差し替える
PUBLISHER_CN="${PUBLISHER_CN:-CN=JapanMarketing}"              # 仮（ばら置き登録は署名を見ないので任意）
PUBLISHER_DISPLAY="${PUBLISHER_DISPLAY:-JapanMarketing LLC}"
VERSION="${VERSION:-0.8.0.0}"
SSHOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -p 2223)
SCPOPT=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -i "$HOME/.ssh/id_ed25519" -P 2223)
S() { ssh "${SSHOPT[@]}" rf@127.0.0.1 "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $*"; }
fail() { echo "FAIL: $*"; exit 1; }
A() {
  local id; id="m$(date +%s)$RANDOM"
  printf '%s\n' "$1" | S "Set-Content -Path C:\\rf\\cmd\\$id.ps1.tmp -Encoding utf8 -Value ([Console]::In.ReadToEnd()); Move-Item C:\\rf\\cmd\\$id.ps1.tmp C:\\rf\\cmd\\$id.ps1 -Force"
  for i in $(seq 1 180); do
    if S "Test-Path C:\\rf\\cmd\\$id.out" | grep -q True; then S "Get-Content C:\\rf\\cmd\\$id.out -Raw; Remove-Item C:\\rf\\cmd\\$id.out -Force"; return 0; fi
    sleep 1
  done
  echo "agent timeout"; return 1
}

[ -f "$PUBLISH/ReplyFive.exe" ] || fail "$PUBLISH/ReplyFive.exe が無い（dotnet publish -r win-arm64）"
echo "== stage（pack.sh と同じ差し替え）"
STAGE="$(mktemp -d)/stage"; mkdir -p "$STAGE"
cp -R "$PUBLISH/." "$STAGE/"
cp -R "$ROOT/packaging/windows/msix/Assets" "$STAGE/Assets"
sed -e "s/REPLACE_PACKAGE_IDENTITY_NAME/$IDENTITY_NAME/" -e "s|REPLACE_PUBLISHER_CN|$PUBLISHER_CN|" -e "s/REPLACE_PUBLISHER_DISPLAY_NAME/$PUBLISHER_DISPLAY/" \
    -e "s/Version=\"[0-9.]*\"/Version=\"$VERSION\"/" -e "s/ProcessorArchitecture=\"[a-z0-9]*\"/ProcessorArchitecture=\"arm64\"/" \
    "$ROOT/packaging/windows/msix/Package.appxmanifest" > "$STAGE/AppxManifest.xml"
find "$STAGE" -name '._*' -delete; find "$STAGE" -name '.DS_Store' -delete
grep -q REPLACE "$STAGE/AppxManifest.xml" && fail "AppxManifest.xml に REPLACE が残っている"
ZIP="$(dirname "$STAGE")/msix-stage.zip"; (cd "$STAGE" && zip -qr "$ZIP" .)
echo "stage: $(du -h "$ZIP" | cut -f1)"

echo "== copy"
S 'Get-Process ReplyFive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Remove-Item C:\rf\msix -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path C:\rf\msix, C:\rf\cmd -Force | Out-Null'
scp "${SCPOPT[@]}" "$ZIP" rf@127.0.0.1:C:/rf/msix.zip || fail "scp"
S 'Expand-Archive -Path C:\rf\msix.zip -DestinationPath C:\rf\msix -Force; Test-Path C:\rf\msix\AppxManifest.xml' | grep -q True || fail "expand"

echo "== developer mode（ばら置き登録に必要。ストア提出には不要）"
S 'New-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Force | Out-Null; Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord; (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock").AllowDevelopmentWithoutDevLicense'

echo "== register"
REG=$(A 'Get-AppxPackage -Name '"$IDENTITY_NAME"' | Remove-AppxPackage -ErrorAction SilentlyContinue; try { Add-AppxPackage -Register C:\rf\msix\AppxManifest.xml -ErrorAction Stop; "registered" } catch { "error: " + $_.Exception.Message }')
echo "$REG" | tr -d '\r' | grep -q registered || fail "Add-AppxPackage: $REG"
A 'Get-AppxPackage -Name '"$IDENTITY_NAME"' | Select-Object Name,Version,Architecture,PackageFullName,Status | Format-List' | tr -d '\r' | grep -E 'Name|Version|Architecture|Status' | head -5

echo "== launch（パッケージのアプリとして起動し、full trust と UI Automation を確かめる）"
A 'Remove-Item "$env:LOCALAPPDATA\ReplyFive\logs\app.log" -Force -ErrorAction SilentlyContinue; $fam = (Get-AppxPackage -Name '"$IDENTITY_NAME"').PackageFamilyName; explorer.exe "shell:AppsFolder\$fam!ReplyFive"; Start-Sleep 12; "processes: " + (Get-Process ReplyFive -ErrorAction SilentlyContinue | Measure-Object).Count'
# full trust の MSIX は %LOCALAPPDATA% を書き換えない（パッケージの LocalCache へは逃げない）ので、通常の場所を見る
LOG=$(A 'Get-Content "$env:LOCALAPPDATA\ReplyFive\logs\app.log" -Tail 10')
echo "$LOG" | tr -d '\r' | grep -E 'launch |growth' | head -5
echo "$LOG" | tr -d '\r' | grep -q 'distribution=store' || fail "配布形態が store にならない（Package.Current の判定、Distribution.cs）"
echo "$LOG" | tr -d '\r' | grep -q 'os=windows' || fail "launch log"

echo "== protocol / startup task"
A 'Get-AppxPackage -Name '"$IDENTITY_NAME"' | Get-AppxPackageManifest | ForEach-Object { $_.Package.Applications.Application.Extensions.Extension } | ForEach-Object { $_.Category } ' | tr -d '\r' | grep -E 'protocol|startupTask|appExecutionAlias'
A '(Get-ItemProperty "HKCU:\Software\Classes\replyfive\shell\open\command" -ErrorAction SilentlyContinue)."(default)"; Get-StartApps | Where-Object Name -eq ReplyFive | ForEach-Object { "startapp: " + $_.AppID }' | tr -d '\r' | grep -E 'startapp|ReplyFive.exe'

echo "== packaged app で run.sh（full trust の MSIX でも UI Automation の読み取り・差し込みが動くか）"
RF_EXE=replyfive.exe SKIP_COPY=1 "$HERE/run.sh" 2>&1 | grep -E '^(==|entry:|FAIL|app:)|platform=|insert trusted|distribution=' || fail "packaged run"
A 'Get-Content "$env:LOCALAPPDATA\ReplyFive\logs\app.log" | Select-String "launch distribution=" | Select-Object -Last 1' | tr -d '\r' | grep -q 'distribution=store' || fail "packaged run が store で動いていない"

echo "== unregister"
A 'Get-Process ReplyFive -ErrorAction SilentlyContinue | Stop-Process -Force; Get-AppxPackage -Name '"$IDENTITY_NAME"' | Remove-AppxPackage; "removed: " + ((Get-AppxPackage -Name '"$IDENTITY_NAME"' | Measure-Object).Count)' | tr -d '\r' | grep removed
rm -rf "$(dirname "$STAGE")"
echo "== done"
