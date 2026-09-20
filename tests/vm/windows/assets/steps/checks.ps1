# Windows 固有の実装が実機で成立しているかの確認（付録CG・CH-2）。本文は出さない。
"== os: " + (Get-CimInstance Win32_OperatingSystem).Caption + " " + (Get-CimInstance Win32_OperatingSystem).Version + " " + $env:PROCESSOR_ARCHITECTURE
"== url scheme: " + (Get-ItemProperty "HKCU:\Software\Classes\replyfive\shell\open\command" -ErrorAction SilentlyContinue)."(default)"
$run = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
"== autostart: " + $(if ($run.ReplyFive) { $run.ReplyFive } else { "(off)" })
$sec = "$env:LOCALAPPDATA\ReplyFive\secrets.dat"
"== dpapi secrets: " + $(if (Test-Path $sec) { "$((Get-Item $sec).Length) bytes" } else { "(none)" })
# DPAPI で復号できるのは同じ利用者だけ（別の利用者が読めないこと自体は VM では試せないので、往復だけ確かめる）
Add-Type -AssemblyName System.Security
$enc = [System.Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes("roundtrip"), $null, "CurrentUser")
"== dpapi roundtrip: " + [Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect($enc, $null, "CurrentUser"))
"== ocr languages: " + ((Get-WindowsCapability -Online -Name "Language.OCR*" | Where-Object State -eq Installed).Name -join ", ")
"== hotkey owner: " + ((Get-Process ReplyFive -ErrorAction SilentlyContinue | Select-Object -First 1).Id)
"== app version: " + (Get-Item C:\rf\app\win-arm64\ReplyFive.exe).VersionInfo.FileVersion
