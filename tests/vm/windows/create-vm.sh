#!/usr/bin/env bash
# Windows 11 ARM64 の検証 VM を UTM に作って無人インストールを始める（付録CH-2）。
# 前提: ~/.cache/utm/iso/windows11-arm64.iso（build-iso.sh）と ~/.cache/utm/iso/utm-guest-tools-replyfive.iso（make-tools-iso.sh）がある。
# 流れ: AppleScript で作成 → UTM を終了して config.plist に表示 virtio-ramfb・SSH 転送 2223→22・音を書く → 起動。
# インストールは応答ファイルで無人（efisys_noprompt で CD から即起動、以後は NVRAM の Windows Boot Manager が先になる）。
# 20〜30 分で自動ログオンし、firstlogon.ps1 が sshd を上げる。確認: ssh -p 2223 rf@127.0.0.1 'type C:\replyfive-ready.txt'
set -euo pipefail
U=/Applications/UTM.app/Contents/MacOS/utmctl
NAME="ReplyFive Windows"
[ -f "$HOME/.cache/utm/iso/windows11-arm64.iso" ] || { echo "windows11-arm64.iso が無い（build-iso.sh）"; exit 1; }
[ -f "$HOME/.cache/utm/iso/utm-guest-tools-replyfive.iso" ] || { echo "utm-guest-tools-replyfive.iso が無い（make-tools-iso.sh）"; exit 1; }
if $U list | grep -q "$NAME"; then echo "既にある: $NAME（消すなら utmctl delete）"; exit 1; fi
open -a UTM; sleep 4
osascript "$(dirname "$0")/create-vm.applescript"
osascript -e 'tell application "UTM" to quit'; sleep 3
D="$HOME/Library/Containers/com.utmapp.UTM/Data/Documents/$NAME.utm/config.plist"
PB=/usr/libexec/PlistBuddy
$PB -c 'Set :System:MemorySize 8192' -c 'Set :System:CPUCount 6' "$D" || true
$PB -c 'Add :Display:0 dict' -c 'Add :Display:0:Hardware string virtio-ramfb' -c 'Add :Display:0:DynamicResolution bool true' -c 'Add :Display:0:NativeResolution bool false' -c 'Add :Display:0:UpscalingFilter string Nearest' -c 'Add :Display:0:DownscalingFilter string Linear' "$D" || $PB -c 'Set :Display:0:Hardware virtio-ramfb' "$D"
$PB -c 'Set :Network:0:Mode Emulated' "$D"
$PB -c 'Add :Network:0:PortForward:0 dict' -c 'Add :Network:0:PortForward:0:Protocol string TCP' -c 'Add :Network:0:PortForward:0:HostAddress string ""' -c 'Add :Network:0:PortForward:0:HostPort integer 2223' -c 'Add :Network:0:PortForward:0:GuestAddress string ""' -c 'Add :Network:0:PortForward:0:GuestPort integer 22' "$D"
$PB -c 'Add :QEMU:AdditionalArguments:0 string -qmp' -c 'Add :QEMU:AdditionalArguments:1 string tcp:127.0.0.1:4445,server,nowait' "$D"   # キー入力・画面取り込み用（assets/qmp.py）
$PB -c 'Add :Sound:0 dict' -c 'Add :Sound:0:Hardware string intel-hda' "$D" || true
plutil -lint "$D"
$PB -c 'Print :Network:0' -c 'Print :Drive' -c 'Print :Display' "$D" | head -60
open -a UTM; sleep 6
$U start "$NAME"; sleep 5; $U list
