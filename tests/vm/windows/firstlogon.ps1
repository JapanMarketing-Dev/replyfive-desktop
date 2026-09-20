# ReplyFive 検証 VM（Windows 11 ARM64、UTM）の初回ログオン設定。autounattend.xml の FirstLogonCommands から実行される。
# ・UTM guest tools（SPICE エージェント・virtio ドライバ）を無人インストール
# ・Win32-OpenSSH（ARM64）を入れて sshd を自動起動、公開鍵を管理者用 authorized_keys へ、既定シェルを PowerShell に
# ・スリープ・画面オフ・スクリーンセーバを止める
$ErrorActionPreference = 'Continue'
$log = 'C:\replyfive-firstlogon.log'
function Log($m) { "$(Get-Date -Format s) $m" | Out-File -FilePath $log -Append -Encoding utf8 }
Log "start user=$env:USERNAME"
$src = Split-Path -Parent $PSScriptRoot   # ISO のルート（setup\ の親）
Log "src=$src"

powercfg /change monitor-timeout-ac 0
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg -h off
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value 0 -ErrorAction SilentlyContinue

# guest tools（NSIS、/S で無人）
$gt = Get-ChildItem -Path $src -Filter 'utm-guest-tools-*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gt) { Log "guest tools: $($gt.FullName)"; Start-Process -FilePath $gt.FullName -ArgumentList '/S' -Wait; Log "guest tools exit" } else { Log "guest tools not found" }

# OpenSSH
$zip = Join-Path $src 'ssh\OpenSSH-ARM64.zip'
$dst = 'C:\Program Files\OpenSSH'
if (Test-Path $zip) {
  Expand-Archive -Path $zip -DestinationPath 'C:\Program Files' -Force
  if (Test-Path 'C:\Program Files\OpenSSH-ARM64') { Move-Item 'C:\Program Files\OpenSSH-ARM64' $dst -Force }
  Log "openssh extracted: $(Test-Path (Join-Path $dst 'sshd.exe'))"
  & powershell -ExecutionPolicy Bypass -File (Join-Path $dst 'install-sshd.ps1') 2>&1 | ForEach-Object { Log "install-sshd: $_" }
  New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue | Out-Null
  New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
  New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -PropertyType String -Force | Out-Null
  $pub = Join-Path $src 'ssh\id_ed25519.pub'
  if (Test-Path $pub) {
    New-Item -ItemType Directory -Path 'C:\ProgramData\ssh' -Force | Out-Null
    Copy-Item $pub 'C:\ProgramData\ssh\administrators_authorized_keys' -Force
    icacls 'C:\ProgramData\ssh\administrators_authorized_keys' /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
    Log "authorized key installed"
  }
  Set-Service -Name sshd -StartupType Automatic
  Start-Service sshd
  Log "sshd status: $((Get-Service sshd).Status)"
} else { Log "openssh zip not found" }

# 接続確認用の印
"ready $(Get-Date -Format s)" | Out-File -FilePath 'C:\replyfive-ready.txt' -Encoding ascii
Log "done"
