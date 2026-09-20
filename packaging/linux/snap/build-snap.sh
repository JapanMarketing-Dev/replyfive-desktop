#!/usr/bin/env bash
# Snap を組む（付録CH）。Linux 実機（snapcraft 8 以上、LXD か Multipass）で実行する。
# 先に README の「先に組む」で packaging/out/linux-<arch>（dotnet publish）を用意する。
# 使い方: build-snap.sh [amd64|arm64]   → clients/desktop/packaging/out/replyfive_<ver>_<arch>.snap
set -euo pipefail
cd "$(dirname "$0")"
ARCH="${1:-amd64}"
case "$ARCH" in amd64) RID=x64;; arm64) RID=arm64;; *) echo "arch は amd64 か arm64"; exit 1;; esac
OUT="../../out"
[ -x "$OUT/linux-$RID/ReplyFive" ] || { echo "$OUT/linux-$RID/ReplyFive が無い。README の dotnet publish を先に実行する"; exit 1; }
# アイコン（snap/gui/replyfive.png は 256×256）
mkdir -p snap/gui
cp ../icons/replyfive-256.png snap/gui/replyfive.png
cp snapcraft.yaml snap/snapcraft.yaml
# metainfo / desktop の版を snapcraft.yaml と揃える
VER="$(grep -E '^version:' snapcraft.yaml | sed -E 's/version: *"?([^"]+)"?/\1/')"
grep -q "version=\"$VER\"" ../app.replyfive.ReplyFive.metainfo.xml || echo "警告: metainfo.xml の releases に $VER が無い"
snapcraft pack --build-for="$ARCH" --output "$OUT/replyfive_${VER}_${ARCH}.snap"
echo "built: $OUT/replyfive_${VER}_${ARCH}.snap"
echo "確認: sudo snap install --dangerous $OUT/replyfive_${VER}_${ARCH}.snap && sudo snap connect replyfive:password-manager-service"
echo "公開: snapcraft upload --release=edge $OUT/replyfive_${VER}_${ARCH}.snap"
