#!/usr/bin/env bash
# UTM の guest tools ISO（~/.cache/utm/iso/utm-guest-tools.iso、https://getutm.app/downloads/utm-guest-tools-latest.iso）を、
# 検証 VM 用の無人インストール応答ファイル（autounattend.xml）・初回ログオン script・Win32-OpenSSH ARM64・SSH 公開鍵を足して作り直す。
#   出力: ~/.cache/utm/iso/utm-guest-tools-replyfive.iso（VM の 2 本目の CD として付ける。Windows Setup が E: の Autounattend.xml を読む）
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ISO_DIR="$HOME/.cache/utm/iso"
SRC="$ISO_DIR/utm-guest-tools.iso"
[ -f "$SRC" ] || curl -L -o "$SRC" https://getutm.app/downloads/utm-guest-tools-latest.iso
ZIP="$HOME/.cache/utm/win/OpenSSH-ARM64.zip"
[ -f "$ZIP" ] || { mkdir -p "$(dirname "$ZIP")"; curl -L -o "$ZIP" https://github.com/PowerShell/Win32-OpenSSH/releases/download/10.0.0.0p2-Preview/OpenSSH-ARM64.zip; }
WORK="$(mktemp -d)"
MNT="$WORK/mnt"; mkdir -p "$MNT" "$WORK/root"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$SRC" >/dev/null
cp -R "$MNT"/. "$WORK/root"/
hdiutil detach "$MNT" >/dev/null
rm -f "$WORK/root/Autounattend.xml"
cp "$HERE/autounattend.xml" "$WORK/root/autounattend.xml"
mkdir -p "$WORK/root/setup" "$WORK/root/ssh"
cp "$HERE/firstlogon.ps1" "$WORK/root/setup/firstlogon.ps1"
cp "$ZIP" "$WORK/root/ssh/OpenSSH-ARM64.zip"
cp "$HOME/.ssh/id_ed25519.pub" "$WORK/root/ssh/id_ed25519.pub"
cp "$HERE/assets/startup.nsh" "$WORK/root/startup.nsh"
OUT="$ISO_DIR/utm-guest-tools-replyfive.iso"
# 同じ inode へ上書き（UTM のブックマークを保つ）
TMP_ISO="$WORK/out.iso"
hdiutil makehybrid -iso -joliet -udf -default-volume-name UTM_TOOLS -o "$TMP_ISO" "$WORK/root" >/dev/null
if [ -f "$OUT" ]; then cat "$TMP_ISO" > "$OUT"; else mv "$TMP_ISO" "$OUT"; fi
rm -rf "$WORK"
echo "ok: $OUT ($(du -h "$OUT" | cut -f1))"
