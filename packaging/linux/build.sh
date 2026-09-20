#!/usr/bin/env bash
# ReplyFive for Linux の配布物を作る（Linux 上で実行。x86_64 / aarch64）。
#   1) dotnet publish（self-contained, 単一ファイル）
#   2) AppImage（直接配布・アプリ内アップデート用。appimagetool が要る）
#   3) .deb（dpkg-deb）
# Flathub / Snap は packaging/linux/flatpak と packaging/linux/snap（付録CH）。ここは直接配布（AppImage・.deb）用。
# 環境変数: SKIP_PUBLISH=1 で dotnet publish を省く（publish 済みを包むだけ。dotnet の無い環境向け）、
#           DIST=<dir> で出力先、ROOT_OVERRIDE=<clients/desktop> で資材の場所を差し替える（コンテナ実行用）。
set -euo pipefail
VERSION="${1:-0.8.0}"
ARCH="${ARCH:-$(uname -m)}"           # x86_64 | aarch64
SENTRY_DSN="${SENTRY_DSN_DESKTOP:-}"
ROOT="${ROOT_OVERRIDE:-$(cd "$(dirname "$0")/../.." && pwd)}"
DIST="${DIST:-$ROOT/dist}"
APPID=app.replyfive.ReplyFive          # ストアと同じ ID（付録CH）。.desktop・アイコン・metainfo の名前に使う
LINUXPKG="$ROOT/packaging/linux"
case "$ARCH" in
  x86_64) RID=linux-x64; DEB_ARCH=amd64 ;;
  aarch64|arm64) RID=linux-arm64; DEB_ARCH=arm64 ;;
  *) echo "unsupported arch $ARCH" >&2; exit 1 ;;
esac
PUBLISH="$DIST/$RID"
mkdir -p "$DIST"

if [ "${SKIP_PUBLISH:-0}" = "1" ]; then
  echo "== publish skipped (using $PUBLISH)"; [ -x "$PUBLISH/ReplyFive" ] || { echo "$PUBLISH/ReplyFive が無い" >&2; exit 1; }
else
echo "== publish $VERSION ($RID)"
dotnet publish "$ROOT/src/ReplyFive.Desktop/ReplyFive.Desktop.csproj" -c Release -f net10.0 -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SentryDsn="$SENTRY_DSN" -o "$PUBLISH"
fi

# --- AppImage ---
echo "== AppImage"
APPDIR="$DIST/ReplyFive.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/icons/hicolor/512x512/apps"
cp -r "$PUBLISH/." "$APPDIR/usr/bin/"
cp "$LINUXPKG/$APPID.desktop" "$APPDIR/usr/share/applications/$APPID.desktop"
cp "$LINUXPKG/$APPID.desktop" "$APPDIR/$APPID.desktop"
cp "$LINUXPKG/$APPID.metainfo.xml" "$APPDIR/usr/share/metainfo/$APPID.metainfo.xml"
cp "$LINUXPKG/icons/replyfive-256.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APPID.png"
cp "$LINUXPKG/icons/replyfive-512.png" "$APPDIR/usr/share/icons/hicolor/512x512/apps/$APPID.png"
cp "$LINUXPKG/icons/replyfive-256.png" "$APPDIR/$APPID.png"
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/replyfive/bundle"
exec "$HERE/usr/bin/ReplyFive" "$@"
EOF
chmod +x "$APPDIR/AppRun"
if command -v appimagetool >/dev/null 2>&1; then
  ARCH="$ARCH" appimagetool "$APPDIR" "$DIST/ReplyFive-$VERSION-$ARCH.AppImage"
  cp "$DIST/ReplyFive-$VERSION-$ARCH.AppImage" "$DIST/ReplyFive.AppImage"
else
  echo "appimagetool not found; AppDir left at $APPDIR" >&2
fi

# --- .deb ---
echo "== deb"
DEBDIR="$DIST/deb"
rm -rf "$DEBDIR"
mkdir -p "$DEBDIR/DEBIAN" "$DEBDIR/opt/replyfive" "$DEBDIR/usr/bin" "$DEBDIR/usr/share/applications" "$DEBDIR/usr/share/metainfo" "$DEBDIR/usr/share/icons/hicolor/256x256/apps" "$DEBDIR/usr/share/icons/hicolor/512x512/apps"
cp -r "$PUBLISH/." "$DEBDIR/opt/replyfive/"
ln -sf /opt/replyfive/ReplyFive "$DEBDIR/usr/bin/replyfive"
sed 's#^Exec=.*#Exec=/opt/replyfive/ReplyFive %u#' "$LINUXPKG/$APPID.desktop" > "$DEBDIR/usr/share/applications/$APPID.desktop"
cp "$LINUXPKG/$APPID.metainfo.xml" "$DEBDIR/usr/share/metainfo/$APPID.metainfo.xml"
cp "$LINUXPKG/icons/replyfive-256.png" "$DEBDIR/usr/share/icons/hicolor/256x256/apps/$APPID.png"
cp "$LINUXPKG/icons/replyfive-512.png" "$DEBDIR/usr/share/icons/hicolor/512x512/apps/$APPID.png"
cat > "$DEBDIR/DEBIAN/control" <<EOF
Package: replyfive
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: JapanMarketing LLC <support@replyfive.app>
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70 | libicu66, libx11-6, libsecret-1-0, at-spi2-core
Recommends: xdotool, wl-clipboard, xclip
Homepage: https://replyfive.app
Description: Turn a short note into a reply you can send
 ReplyFive reads the conversation on screen (via AT-SPI) and turns what you
 want to say into a polished reply, inserted into the field you were typing in.
EOF
cat > "$DEBDIR/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
exit 0
EOF
chmod 755 "$DEBDIR/DEBIAN/postinst"
if command -v dpkg-deb >/dev/null 2>&1; then
  dpkg-deb --build --root-owner-group "$DEBDIR" "$DIST/replyfive_${VERSION}_${DEB_ARCH}.deb"
else
  echo "dpkg-deb not found; deb tree left at $DEBDIR" >&2
fi
echo "== done: $DIST"
