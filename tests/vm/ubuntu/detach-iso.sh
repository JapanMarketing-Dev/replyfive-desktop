#!/usr/bin/env bash
# 無人インストール後（VM が stopped）に ISO とシードの CD を外して起動する。
set -euo pipefail
U=/Applications/UTM.app/Contents/MacOS/utmctl
NAME="${1:-ReplyFive Linux}"
$U list | grep "$NAME" | grep -q stopped || { echo "$NAME は停止していない"; exit 1; }
osascript -e 'tell application "UTM" to quit'; sleep 3
D="$HOME/Library/Containers/com.utmapp.UTM/Data/Documents/$NAME.utm/config.plist"
PB=/usr/libexec/PlistBuddy
# CD（ImageType=CD）を先頭から順に消す
while $PB -c 'Print :Drive:0:ImageType' "$D" 2>/dev/null | grep -q CD; do $PB -c 'Delete :Drive:0' "$D"; done
plutil -lint "$D"; $PB -c 'Print :Drive' "$D" | grep -c ImageName
open -a UTM; sleep 6; $U start "$NAME"; sleep 3; $U list
