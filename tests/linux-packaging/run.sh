#!/usr/bin/env bash
# Linux 配布物をコンテナで作る（この Mac では podman machine、CI では docker）。出力は dist/ReplyFive*.AppImage と dist/replyfive_*.deb。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ENGINE="${ENGINE:-podman}"
ARCH="${ARCH:-$(uname -m)}"
case "$ARCH" in arm64|aarch64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
[ -x "$ROOT/dist/$RID/ReplyFive" ] || { echo "dist/$RID/ReplyFive が無い。先に dotnet publish する（docs/desktop-tech-stack.md）"; exit 1; }
$ENGINE build -t replyfive-linux-pkg "$HERE"
$ENGINE run --rm -e VERSION="${VERSION:-0.8.0}" -v "$ROOT:/src:ro" -v "$ROOT/dist:/work/dist" replyfive-linux-pkg
