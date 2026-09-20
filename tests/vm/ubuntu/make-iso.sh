#!/usr/bin/env bash
# Ubuntu 24.04 arm64 の無人インストール用 ISO とシード（cidata）を作る（付録CH-2）。
#   ~/.cache/utm/iso/ubuntu-autoinstall.iso : 公式 live-server ISO の grub.cfg に `autoinstall` を足した remaster（確認プロンプトを飛ばす）
#   ~/.cache/utm/seed/seed.iso              : cloud-init NoCloud（user-data / meta-data）。利用者 rf / replyfive、SSH は ~/.ssh/id_ed25519.pub
# 要: xorriso（brew install xorriso）、hdiutil、openssl
set -euo pipefail
VER="${UBUNTU_VERSION:-24.04.5}"
ISO_DIR="$HOME/.cache/utm/iso"; SEED_DIR="$HOME/.cache/utm/seed"
mkdir -p "$ISO_DIR" "$SEED_DIR/cidata"
SRC="$ISO_DIR/ubuntu-$VER-live-server-arm64.iso"
if [ ! -f "$SRC" ]; then
  curl -L -o "$SRC" "https://cdimage.ubuntu.com/releases/24.04/release/ubuntu-$VER-live-server-arm64.iso"
fi
EXPECTED="$(curl -sSL https://cdimage.ubuntu.com/releases/24.04/release/SHA256SUMS | grep " \*ubuntu-$VER-live-server-arm64.iso" | cut -d' ' -f1)"
[ "$(shasum -a 256 "$SRC" | cut -d' ' -f1)" = "$EXPECTED" ] || { echo "SHA256 が合わない: $SRC"; exit 1; }

# grub.cfg に autoinstall を足す
TMP="$(mktemp -d)"
xorriso -osirrox on -indev "$SRC" -extract /boot/grub/grub.cfg "$TMP/grub.cfg" 2>/dev/null
sed -e 's/^set timeout=30/set timeout=3/' -e 's#\(linux\t/casper/[a-z-]*vmlinuz\)  ---#\1 autoinstall ---#' "$TMP/grub.cfg" > "$TMP/grub.auto.cfg"
grep -q 'autoinstall ---' "$TMP/grub.auto.cfg" || { echo "grub.cfg の書き換えに失敗"; exit 1; }
rm -f "$ISO_DIR/ubuntu-autoinstall.iso"
xorriso -indev "$SRC" -outdev "$ISO_DIR/ubuntu-autoinstall.iso" -boot_image any replay -map "$TMP/grub.auto.cfg" /boot/grub/grub.cfg -end >/dev/null 2>&1

# シード（user-data）
HASH="$(openssl passwd -6 replyfive)"
PUB="$(cat "$HOME/.ssh/id_ed25519.pub")"
cat > "$SEED_DIR/cidata/user-data" <<EOF
#cloud-config
autoinstall:
  version: 1
  locale: en_US.UTF-8
  keyboard: {layout: us}
  timezone: Asia/Tokyo
  identity: {hostname: replyfive-linux, username: rf, password: "$HASH"}
  ssh: {install-server: true, allow-pw: true, authorized-keys: ["$PUB"]}
  storage: {layout: {name: direct}}
  apt: {geoip: true}
  packages: [ubuntu-desktop-minimal, gnome-keyring, at-spi2-core, xdotool, wl-clipboard, xclip, libsecret-1-0, dbus-x11, gnome-terminal, gnome-screenshot, flatpak, flatpak-builder, appstream, desktop-file-utils, curl, git, python3, spice-vdagent, qemu-guest-agent]
  late-commands:
    - curtin in-target -- bash -c 'echo "rf ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/rf && chmod 440 /etc/sudoers.d/rf'
    - curtin in-target -- systemctl set-default graphical.target
    - curtin in-target -- bash -c 'mkdir -p /etc/gdm3 && printf "[daemon]\\nAutomaticLoginEnable=true\\nAutomaticLogin=rf\\n" > /etc/gdm3/custom.conf'
    - curtin in-target -- systemctl enable ssh
  shutdown: poweroff
EOF
printf 'instance-id: replyfive-linux\nlocal-hostname: replyfive-linux\n' > "$SEED_DIR/cidata/meta-data"
# 注意: seed.iso を消して作り直すと UTM のブックマークが無効になる（VM を作り直す）。上書きで済ませたいときは同じ inode に書く。
rm -f "$SEED_DIR/seed.iso"
hdiutil makehybrid -iso -joliet -default-volume-name cidata -o "$SEED_DIR/seed.iso" "$SEED_DIR/cidata" >/dev/null
rm -rf "$TMP"
echo "ok: $ISO_DIR/ubuntu-autoinstall.iso, $SEED_DIR/seed.iso"
