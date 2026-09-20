#!/usr/bin/env bash
# clients/desktop（と shared/ の契約・fixtures）を公開リポジトリ JapanMarketing-Dev/replyfive-desktop へ同期する（付録CH-2）。
# Flathub はソースからのビルドが原則なので、この公開リポジトリのタグを Flatpak マニフェストが参照する。
# 使い方: packaging/publish-source.sh [tag]     例: packaging/publish-source.sh desktop-v0.8.0
#   DEST=<dir> で作業ディレクトリ（既定 ~/.cache/replyfive-desktop-public）。push は SSH（git@github.com）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"            # monorepo root
SRC="$ROOT/clients/desktop"
DEST="${DEST:-$HOME/.cache/replyfive-desktop-public}"
REMOTE="git@github.com:JapanMarketing-Dev/replyfive-desktop.git"
TAG="${1:-}"

if [ ! -d "$DEST/.git" ]; then
  mkdir -p "$DEST"; git -C "$DEST" init -b main >/dev/null
  git -C "$DEST" remote add origin "$REMOTE"
  git -C "$DEST" fetch origin main >/dev/null 2>&1 && git -C "$DEST" reset --soft origin/main >/dev/null 2>&1 || true
fi

# 公開しないもの：ビルド中間物・配布物・環境ファイル・IDE 設定・内部証跡
rsync -a --delete \
  --exclude 'bin/' --exclude 'obj/' --exclude 'dist/' --exclude '.env*' --exclude '*.user' --exclude '.vs/' --exclude '.idea/' \
  --exclude 'packaging/out/' --exclude 'packaging/linux/snap/snap/' --exclude 'tests/*/shots/' \
  "$SRC/" "$DEST/" --exclude '.git/'
mkdir -p "$DEST/shared"
rsync -a --delete "$ROOT/shared/fixtures/" "$DEST/shared/fixtures/"
cp "$ROOT/shared/replyfive-contract.json" "$DEST/shared/replyfive-contract.json"
cp "$SRC/packaging/public/LICENSE" "$DEST/LICENSE"
cp "$SRC/packaging/public/README.public.md" "$DEST/README.md"
cp "$SRC/packaging/public/gitignore" "$DEST/.gitignore"

# 公開前の最終確認：秘密値らしき文字列が無いこと
if grep -rniE 'sk_(live|test)_[A-Za-z0-9]{8,}|AKIA[0-9A-Z]{12,}|BEGIN (RSA|EC|OPENSSH) PRIVATE' "$DEST" --exclude-dir=.git -l | head -1 | grep -q .; then
  echo "秘密値らしき文字列がある。公開を中止" >&2; exit 1
fi

MONO_SHA="$(git -C "$ROOT" rev-parse --short HEAD)"
git -C "$DEST" add -A
if git -C "$DEST" diff --cached --quiet; then echo "変更なし"; else
  git -C "$DEST" -c user.name='JapanMarketing' -c user.email='support@replyfive.app' commit -q -m "Sync from ReplyFive monorepo $MONO_SHA ($(date +%Y-%m-%d))"
fi
git -C "$DEST" push -u origin main
if [ -n "$TAG" ]; then
  git -C "$DEST" tag -f "$TAG"; git -C "$DEST" push -f origin "$TAG"
  echo "tag $TAG -> $(git -C "$DEST" rev-parse "$TAG")"
fi
echo "published: https://github.com/JapanMarketing-Dev/replyfive-desktop ($(git -C "$DEST" rev-parse --short HEAD))"
