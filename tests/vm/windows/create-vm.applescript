-- UTM に Windows 11 ARM64 の検証 VM を作る（create-vm.sh から呼ぶ）。作成だけ行い、細かい設定は create-vm.sh が config.plist を直接書く。
-- drives: 1 本目 = Windows のインストール ISO（UUP dump で作った Pro en-US）、2 本目 = 応答ファイル入りの guest tools ISO（E: になる）、3 本目 = NVMe 64 GiB
set home to POSIX path of (path to home folder)
set iso to POSIX file (home & ".cache/utm/iso/windows11-arm64.iso")
set tools to POSIX file (home & ".cache/utm/iso/utm-guest-tools-replyfive.iso")
tell application "UTM"
	set vm to make new virtual machine with properties {backend:qemu, configuration:{name:"ReplyFive Windows", architecture:"aarch64", memory:8192, cpu cores:6, drives:{{removable:true, source:iso}, {removable:true, source:tools}, {guest size:65536, interface:NVMe}}}}
	return id of vm
end tell
