#!/usr/bin/env bash
# Windows 11 ARM64（Pro、en-US）のインストール ISO をこの Mac で作る（付録CH-2）。
# Microsoft の直接ダウンロード（software-download/windows11arm64）は自動化アクセスを Sentinel が拒否するので、UUP dump から
# Windows Update の配信ファイルを取り、uup-converter（wimlib）で ISO にする。CrystalFetch が GUI でやっていることと同じ。
#   要: brew install aria2 cabextract wimlib cdrtools xorriso、~/.cache/utm/bin/chntpw（chntpw の reged を cc で組んだもの。brew の chntpw は CLT が古いと組めない）
#   出力: ~/.cache/utm/iso/windows11-arm64.iso
# 注意: uup-converter の mkisofs 呼び出しは El Torito を BIOS platform で書くため UTM の edk2 から素直に起動できない。
#       できた ISO を展開し xorriso で作り直す。El Torito は 2 本：1 本目 efisys.bin（「Press any key」で 5 秒待って戻る＝インストール後の再起動で
#       ディスクへ落ちる）、2 本目 efisys_noprompt.bin（startup.nsh が最初の起動でこれを選び、キー入力なしで Setup に入る）。
set -euo pipefail
BUILD_ID="${UUP_BUILD_ID:-abe72dd0-5e7f-4bac-97be-ba53277200dd}"   # Windows 11, version 25H2 (26200.9457) arm64。api.uupdump.net/listid.php?search=arm64 で最新を探す
EDITION="${UUP_EDITION:-professional}"; LANG_="${UUP_LANG:-en-us}"
WORK="$HOME/.cache/utm/uup/pkg"; OUT="$HOME/.cache/utm/iso/windows11-arm64.iso"
export PATH="$HOME/.cache/utm/bin:/opt/homebrew/bin:$PATH"
for p in aria2c cabextract wimlib-imagex chntpw mkisofs xorriso; do command -v $p >/dev/null || { echo "$p が無い"; exit 1; }; done
mkdir -p "$WORK"; cd "$WORK"
if [ ! -f files/convert.sh ]; then
  curl -sL -o pkg.zip -X POST -d 'autodl=2&updates=1&cleanup=1' "https://uupdump.net/get.php?id=$BUILD_ID&pack=$LANG_&edition=$EDITION"
  unzip -o -q pkg.zip; chmod +x files/convert.sh 2>/dev/null || true
  aria2c --no-conf --console-log-level=warn -x16 -s16 -j2 --allow-overwrite=true --auto-file-renaming=false -d files -i files/converter_multi
  chmod +x files/convert.sh
fi
aria2c --no-conf --console-log-level=warn -o aria2_script.txt --allow-overwrite=true --auto-file-renaming=false "https://uupdump.net/get.php?id=$BUILD_ID&pack=$LANG_&edition=$EDITION&aria2=2"
grep -q '#UUPDUMP_ERROR' aria2_script.txt && { grep UUPDUMP_ERROR aria2_script.txt; exit 1; }
aria2c --no-conf --console-log-level=warn -x16 -s16 -j5 -c -R -d UUPs -i aria2_script.txt
./files/convert.sh wim UUPs 0
FIRST="$(ls -t *.ISO *.iso 2>/dev/null | head -1)"; [ -n "$FIRST" ] || { echo "ISO ができていない"; exit 1; }
# El Torito を UEFI platform で付け直す
TMP="$(mktemp -d)"; MNT="$TMP/mnt"; mkdir -p "$MNT" "$TMP/root"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$FIRST" >/dev/null
rsync -a "$MNT/" "$TMP/root/"; hdiutil detach "$MNT" >/dev/null
rm -f "$TMP/root/boot.catalog"
LABEL="$(basename "$FIRST" | sed 's/\.[Ii][Ss][Oo]$//' | cut -c1-32)"
xorriso -as mkisofs -iso-level 3 -J -joliet-long -V "$LABEL" -e efi/microsoft/boot/efisys.bin -no-emul-boot -eltorito-alt-boot -e efi/microsoft/boot/efisys_noprompt.bin -no-emul-boot -o "$TMP/out.iso" "$TMP/root" 2>&1 | tail -1
if [ -f "$OUT" ]; then cat "$TMP/out.iso" > "$OUT"; else mv "$TMP/out.iso" "$OUT"; fi   # 同じ inode へ（UTM のブックマークを保つ）
rm -rf "$TMP" "$FIRST"
xorriso -indev "$OUT" -report_el_torito plain 2>/dev/null | grep 'boot img'
echo "ok: $OUT"
