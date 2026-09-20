#!/usr/bin/env bash
# Windows 無しで MSIX を作って検証する（付録CH）。Microsoft の msix-packaging（makemsix）を macOS / Linux でビルドして使う。
# ストア提出物はストア側が署名するので署名は要らない。Identity の REPLACE_* は Partner Center の値が出るまで仮の値で埋める（構文検証用）。
#   makemsix のビルド（macOS）: git clone https://github.com/microsoft/msix-packaging && cd msix-packaging && mkdir .vs && cd .vs &&
#     cmake -DMACOS=on -DCMAKE_BUILD_TYPE=Release -DMSIX_PACK=on -DUSE_VALIDATION_PARSER=on -DCMAKE_SHARED_LINKER_FLAGS=-lz -DCMAKE_EXE_LINKER_FLAGS=-lz .. && make -j8
#   使い方: MAKEMSIX=/path/to/makemsix tests/msix-pack/pack.sh 0.8.0.0   → packaging/out/msix/ReplyFive_<ver>_<arch>.msix と .msixbundle
set -euo pipefail
VERSION="${1:-0.8.0.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
MAKEMSIX="${MAKEMSIX:-makemsix}"
OUT="$ROOT/packaging/out"; MSIXDIR="$OUT/msix"; rm -rf "$MSIXDIR"; mkdir -p "$MSIXDIR/bundle"
IDENTITY_NAME="${IDENTITY_NAME:-JapanMarketing.ReplyFive}"          # 仮。Partner Center の Product identity に差し替える
PUBLISHER_CN="${PUBLISHER_CN:-CN=REPLACE-PUBLISHER}"                 # 仮
PUBLISHER_DISPLAY="${PUBLISHER_DISPLAY:-JapanMarketing LLC}"
for arch in x64 arm64; do
  publish="$OUT/win-$arch"
  [ -f "$publish/ReplyFive.exe" ] || { echo "$publish/ReplyFive.exe が無い（README の dotnet publish を先に）"; exit 1; }
  stage="$MSIXDIR/stage-$arch"; rm -rf "$stage"; mkdir -p "$stage"
  cp -R "$publish/." "$stage/"
  cp -R "$ROOT/packaging/windows/msix/Assets" "$stage/Assets"
  sed -e "s/REPLACE_PACKAGE_IDENTITY_NAME/$IDENTITY_NAME/" -e "s/REPLACE_PUBLISHER_CN/$PUBLISHER_CN/" -e "s/REPLACE_PUBLISHER_DISPLAY_NAME/$PUBLISHER_DISPLAY/" \
      -e "s/Version=\"[0-9.]*\"/Version=\"$VERSION\"/" -e "s/ProcessorArchitecture=\"[a-z0-9]*\"/ProcessorArchitecture=\"$arch\"/" \
      "$ROOT/packaging/windows/msix/Package.appxmanifest" > "$stage/AppxManifest.xml"
  find "$stage" -name '._*' -delete; find "$stage" -name '.DS_Store' -delete
  msix="$MSIXDIR/ReplyFive_${VERSION}_$arch.msix"
  echo "== pack ${arch}"; "$MAKEMSIX" pack -d "$stage" -p "$msix"
  echo "== verify ${arch}（unpack して中身を読む）"; rm -rf "$MSIXDIR/check-$arch"; "$MAKEMSIX" unpack -p "$msix" -d "$MSIXDIR/check-$arch" -ss
  ls "$MSIXDIR/check-$arch" | grep -E 'AppxManifest.xml|AppxBlockMap.xml|ReplyFive.exe|Assets' | tr '\n' ' '; echo
  grep -o 'ProcessorArchitecture="[a-z0-9]*"' "$MSIXDIR/check-$arch/AppxManifest.xml"
  cp "$msix" "$MSIXDIR/bundle/"
done
echo "== bundle（makemsix は未署名の .msix を bundle に入れられない。失敗したら Partner Center へ x64 / arm64 の .msix を別々に上げるか、Windows の makeappx（build-msix.ps1）で bundle する）"
"$MAKEMSIX" bundle -d "$MSIXDIR/bundle" -p "$MSIXDIR/ReplyFive_$VERSION.msixbundle" 2>&1 | grep -v 'Copyright\|Microsoft (R)' || true
ls -la "$MSIXDIR"/*.msix "$MSIXDIR"/*.msixbundle 2>/dev/null
echo "== done: $MSIXDIR"
