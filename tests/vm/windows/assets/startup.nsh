@echo -off
# UTM の UEFI（edk2）は USB 接続の CD から起動するが、Windows の cdboot は「Press any key to boot from CD or DVD」で止まり、
# 時間切れでシェルへ落ちる。シェルは fsN:\startup.nsh を順に探して実行するので、この script（tools ISO のルート）が
# 1) インストール済みディスクの Windows Boot Manager があればそれを、2) 無ければ ISO の El Torito イメージ（efisys_noprompt.bin の FAT、edk2 では fs0）にある bootaa64.efi（キー入力待ちなし）を起動する。
# build-iso.sh が xorriso で EFI プラットフォームの El Torito を正しく付けていれば firmware が直接起動し、ここへは来ない。
for %i in fs0 fs1 fs2 fs3 fs4 fs5
  if exist %i:\EFI\Microsoft\Boot\bootmgfw.efi then
    echo ReplyFive: boot %i:\EFI\Microsoft\Boot\bootmgfw.efi
    %i:\EFI\Microsoft\Boot\bootmgfw.efi
  endif
endfor
for %i in fs0 fs1 fs2 fs3 fs4 fs5
  if exist %i:\efi\boot\bootaa64.efi then
    echo ReplyFive: boot %i:\efi\boot\bootaa64.efi
    %i:\efi\boot\bootaa64.efi
  endif
endfor
