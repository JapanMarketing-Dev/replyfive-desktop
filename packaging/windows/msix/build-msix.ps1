<#
.SYNOPSIS
  Microsoft Store 用の MSIX bundle（x64 + arm64）と .msixupload を作る（付録CH）。Windows SDK の makeappx / signtool が要る。
.EXAMPLE
  pwsh clients/desktop/packaging/windows/msix/build-msix.ps1 -Version 0.8.0.0
  pwsh clients/desktop/packaging/windows/msix/build-msix.ps1 -Version 0.8.0.0 -SelfSign   # 手元で動かす（自己署名を信頼ストアへ入れる）
.NOTES
  先に README の「先に組む」で packaging/out/win-x64 と win-arm64 を用意する（dotnet publish）。
  ストア提出はストア側が署名するので -SelfSign は付けない。
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,   # 0.8.0.0（4 桁。ストアは最後の桁 0 を推奨）
  [switch]$SelfSign,
  [string]$SentryDsn = $env:SENTRY_DSN_DESKTOP
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')      # clients/desktop/packaging
$out = Join-Path $root 'out'
$msixDir = Join-Path $out 'msix'
New-Item -ItemType Directory -Force $msixDir | Out-Null

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory | Where-Object Name -match '^10\.' | Sort-Object Name -Descending | Select-Object -First 1
$makeappx = Join-Path $sdk.FullName 'x64\makeappx.exe'
$signtool = Join-Path $sdk.FullName 'x64\signtool.exe'
if (-not (Test-Path $makeappx)) { throw "makeappx.exe が無い（Windows SDK を入れる）: $makeappx" }

$packages = @()
foreach ($arch in 'x64', 'arm64') {
  $publish = Join-Path $out "win-$arch"
  if (-not (Test-Path (Join-Path $publish 'ReplyFive.exe'))) { throw "$publish に ReplyFive.exe が無い。README の dotnet publish を先に実行する" }

  $stage = Join-Path $msixDir "stage-$arch"
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
  New-Item -ItemType Directory -Force $stage | Out-Null
  Copy-Item -Recurse (Join-Path $publish '*') $stage
  Copy-Item -Recurse (Join-Path $PSScriptRoot 'Assets') (Join-Path $stage 'Assets')

  # マニフェスト：版とアーキテクチャを差し替える
  $manifest = Get-Content (Join-Path $PSScriptRoot 'Package.appxmanifest') -Raw
  if ($manifest -match 'REPLACE_') { Write-Warning 'Package.appxmanifest に REPLACE_ が残っている（Partner Center の Product identity を写す）' }
  $manifest = $manifest -replace 'Version="[0-9.]+"', "Version=`"$Version`"" -replace 'ProcessorArchitecture="[a-z0-9]+"', "ProcessorArchitecture=`"$arch`""
  Set-Content (Join-Path $stage 'AppxManifest.xml') $manifest -Encoding utf8

  $msix = Join-Path $msixDir "ReplyFive_${Version}_$arch.msix"
  & $makeappx pack /d $stage /p $msix /o
  $packages += $msix
}

# bundle と、ストアへ上げる .msixupload（bundle + シンボルの zip。シンボル無しでも可）
$bundleDir = Join-Path $msixDir 'bundle'
if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
New-Item -ItemType Directory -Force $bundleDir | Out-Null
$packages | ForEach-Object { Copy-Item $_ $bundleDir }
$bundle = Join-Path $msixDir "ReplyFive_$Version.msixbundle"
& $makeappx bundle /d $bundleDir /p $bundle /o

if ($SelfSign) {
  $cn = 'CN=ReplyFive Dev'
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $cn | Select-Object -First 1
  if (-not $cert) { $cert = New-SelfSignedCertificate -Type Custom -Subject $cn -KeyUsage DigitalSignature -FriendlyName 'ReplyFive Dev' -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') }
  & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $bundle
  Write-Host "自己署名で署名した。信頼させるには証明書を「信頼されたユーザー」へ: Export-Certificate → certutil -addstore TrustedPeople"
}

$upload = Join-Path $out "ReplyFive_$Version.msixupload"
if (Test-Path $upload) { Remove-Item -Force $upload }
Compress-Archive -Path $bundle -DestinationPath ($upload -replace '\.msixupload$', '.zip')
Move-Item ($upload -replace '\.msixupload$', '.zip') $upload
Write-Host "built: $upload"
