-- UTM に Ubuntu 24.04 (arm64) の検証 VM を作る（create-vm.sh から呼ぶ）。作成だけ行い、細かい設定は create-vm.sh が config.plist を直接書く
-- （AppleScript の update configuration は UTM 4.7.5 で型変換エラーになるため）。
set home to POSIX path of (path to home folder)
set iso to POSIX file (home & ".cache/utm/iso/ubuntu-autoinstall.iso")
set seed to POSIX file (home & ".cache/utm/seed/seed.iso")
tell application "UTM"
	set vm to make new virtual machine with properties {backend:qemu, configuration:{name:"ReplyFive Linux", architecture:"aarch64", drives:{{removable:true, source:iso}, {removable:true, source:seed}, {guest size:65536}}}}
	return id of vm
end tell
