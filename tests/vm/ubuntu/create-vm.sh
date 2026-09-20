#!/usr/bin/env bash
# Ubuntu 24.04 arm64 の検証 VM を UTM に作って無人インストールを始める（付録CH-2）。
# 前提: make-iso.sh で ~/.cache/utm/iso/ubuntu-autoinstall.iso と ~/.cache/utm/seed/seed.iso を作ってある。
# 流れ: AppleScript で作成 → UTM を終了して config.plist にメモリ 8G・6 コア・virtio-gpu-gl・SSH 転送 2222→22 を書く → 起動。
# インストールは autoinstall（shutdown: poweroff）。VM が stopped になったら detach-iso.sh で ISO を外して起動する。
set -euo pipefail
U=/Applications/UTM.app/Contents/MacOS/utmctl
NAME="ReplyFive Linux"
if $U list | grep -q "$NAME"; then echo "既にある: $NAME（消すなら utmctl delete）"; exit 1; fi
open -a UTM; sleep 4
osascript "$(dirname "$0")/create-vm.applescript"
osascript -e 'tell application "UTM" to quit'; sleep 3
D="$HOME/Library/Containers/com.utmapp.UTM/Data/Documents/$NAME.utm/config.plist"
PB=/usr/libexec/PlistBuddy
$PB -c 'Set :System:MemorySize 8192' -c 'Set :System:CPUCount 6' "$D"
$PB -c 'Add :Display:0 dict' -c 'Add :Display:0:Hardware string virtio-gpu-gl-pci' -c 'Add :Display:0:DynamicResolution bool true' -c 'Add :Display:0:NativeResolution bool false' -c 'Add :Display:0:UpscalingFilter string Nearest' -c 'Add :Display:0:DownscalingFilter string Linear' "$D"
$PB -c 'Set :Network:0:Mode Emulated' "$D"   # 共有（vmnet）のままだとポート転送が効かない
$PB -c 'Add :QEMU:AdditionalArguments:0 string -qmp' -c 'Add :QEMU:AdditionalArguments:1 string tcp:127.0.0.1:4446,server,nowait' "$D"   # キー入力・画面取り込み（../windows/assets/qmp.py、QMP_PORT=4446）
$PB -c 'Add :Network:0:PortForward:0 dict' -c 'Add :Network:0:PortForward:0:Protocol string TCP' -c 'Add :Network:0:PortForward:0:HostAddress string ""' -c 'Add :Network:0:PortForward:0:HostPort integer 2222' -c 'Add :Network:0:PortForward:0:GuestAddress string ""' -c 'Add :Network:0:PortForward:0:GuestPort integer 22' "$D"
$PB -c 'Add :Sound:0 dict' -c 'Add :Sound:0:Hardware string intel-hda' "$D" || true
plutil -lint "$D"
open -a UTM; sleep 6
$U start "$NAME"; sleep 5; $U list
