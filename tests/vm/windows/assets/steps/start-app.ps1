# ReplyFive を起動する。引数で実行ファイルを指定できる（MSIX は実行エイリアス replyfive.exe。
# ばら置きの exe を直接起動するとパッケージ識別が付かず、配布形態が store にならない）
param([string]$Exe = "C:\rf\app\win-arm64\ReplyFive.exe")
if ($Exe -eq "replyfive.exe") { Start-Process -FilePath $Exe } else { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) }
Start-Sleep 8
"app: $Exe"
