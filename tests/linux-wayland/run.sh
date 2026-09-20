#!/usr/bin/env bash
# Wayland 経路の結合テスト（podman / docker）。前提は linux-integration と同じ（dist/linux-<arch>、dist/server-linux-<arch>）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
ENGINE="${ENGINE:-podman}"; ARCH="${ARCH:-$(uname -m)}"
case "$ARCH" in arm64|aarch64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
mkdir -p "$ROOT/dist/shots"
$ENGINE build -t replyfive-linux-wayland "$HERE"
$ENGINE run --rm -v "$ROOT/dist/$RID:/work/app:ro" -v "$ROOT/dist/server-$RID:/work/server:ro" -v "$ROOT/dist/shots:/work/shots" replyfive-linux-wayland
