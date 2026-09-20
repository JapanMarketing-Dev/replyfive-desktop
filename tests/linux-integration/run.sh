#!/usr/bin/env bash
# Linux 層の結合テストをコンテナで実行する（この Mac では podman machine、CI では docker）。
# 前提: dist/linux-<arch> に ReplyFive を publish 済み（PublishSingleFile）、dist/server-linux-<arch>/replyfive にサーバを GOOS=linux でビルド済み。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ENGINE="${ENGINE:-podman}"
ARCH="${ARCH:-$(uname -m)}"
case "$ARCH" in arm64|aarch64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
$ENGINE build -t replyfive-linux-test "$HERE"
mkdir -p "$ROOT/dist/shots"
$ENGINE run --rm -v "$ROOT/dist/$RID:/work/app:ro" -v "$ROOT/dist/server-$RID:/work/server:ro" -v "$ROOT/dist:/work/dist:ro" -v "$ROOT/dist/shots:/work/shots" replyfive-linux-test
