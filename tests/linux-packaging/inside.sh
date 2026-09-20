#!/usr/bin/env bash
# コンテナ内：/src（clients/desktop、読み取り専用）と /work/dist（publish 済み + 出力）で build.sh の AppImage / .deb 部分を実行し、検証する。
set -euo pipefail
export APPIMAGE_EXTRACT_AND_RUN=1
VERSION="${VERSION:-0.8.0}"
cp -r /src/packaging /work/packaging
mkdir -p /work/src
SKIP_PUBLISH=1 DIST=/work/dist ROOT_OVERRIDE=/src bash /work/packaging/linux/build.sh "$VERSION"
echo "== validate"
desktop-file-validate /work/dist/ReplyFive.AppDir/usr/share/applications/app.replyfive.ReplyFive.desktop && echo "desktop: ok"
appstreamcli validate --no-net /src/packaging/linux/app.replyfive.ReplyFive.metainfo.xml || true
echo "== AppImage smoke"
ls -la /work/dist/*.AppImage
/work/dist/ReplyFive.AppImage --version
echo "== deb smoke"
dpkg-deb --info /work/dist/replyfive_*.deb | sed -n 1,20p
apt-get update >/dev/null 2>&1
apt-get install -y /work/dist/replyfive_*.deb 2>&1 | tail -3
dpkg -s replyfive | grep -E 'Status|Version'
/opt/replyfive/ReplyFive --version
replyfive --version
desktop-file-validate /usr/share/applications/app.replyfive.ReplyFive.desktop && echo "installed desktop: ok"
echo "PASS"
