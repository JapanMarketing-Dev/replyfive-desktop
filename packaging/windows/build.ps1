# ReplyFive for Windows の配布物を作る（Windows 上で実行）。
#   1) dotnet publish（self-contained, win-x64, 単一ファイル）
#   2) Inno Setup で署名付きインストーラ（直接配布・アプリ内アップデート用）
#   3) MSIX（Microsoft Store 提出用）
# 前提: .NET 10 SDK、Inno Setup 6（ISCC.exe）、Windows SDK（makeappx.exe / signtool.exe）。
# 署名: 直接配布は Azure Trusted Signing か EV 証明書（-SignCommand で渡す）。Store 提出の MSIX はストアが署名する。
param(
  [string]$Version = "0.8.0",
  [string]$SentryDsn = "",
  [string]$SignCommand = "",           # 例: 'signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a "{file}"'
  [ValidateSet("x64", "arm64")][string]$Arch = "x64",   # 付録CI-4：arm64 は ReplyFive-Setup-arm64.exe / ReplyFive-<ver>-arm64.msix
  [switch]$SkipInstaller,
  [switch]$SkipMsix
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dist = Join-Path $root "dist"
$publish = Join-Path $dist "win-$Arch"
$suffix = if ($Arch -eq "x64") { "" } else { "-$Arch" }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "== publish $Version ($Arch)"
dotnet publish (Join-Path $root "src\ReplyFive.Desktop\ReplyFive.Desktop.csproj") -c Release -f net10.0-windows10.0.19041.0 -r "win-$Arch" --self-contained true `
  -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SentryDsn=$SentryDsn -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
if ($SignCommand) { Invoke-Expression ($SignCommand -replace "\{file\}", (Join-Path $publish "ReplyFive.exe")) }

if (-not $SkipInstaller) {
  Write-Host "== installer"
  $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
  if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
  & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DArch=$Arch" (Join-Path $PSScriptRoot "ReplyFive.iss")
  if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
  if ($SignCommand) { Invoke-Expression ($SignCommand -replace "\{file\}", (Join-Path $dist "ReplyFive-Setup$suffix.exe")) }
  Copy-Item (Join-Path $dist "ReplyFive-Setup$suffix.exe") (Join-Path $dist "ReplyFive-Setup-$Version$suffix.exe") -Force
}

if (-not $SkipMsix) {
  Write-Host "== msix"
  $msixRoot = Join-Path $dist "msix"
  if (Test-Path $msixRoot) { Remove-Item $msixRoot -Recurse -Force }
  New-Item -ItemType Directory -Force (Join-Path $msixRoot "app") | Out-Null
  Copy-Item (Join-Path $publish "*") (Join-Path $msixRoot "app") -Recurse -Force
  New-Item -ItemType Directory -Force (Join-Path $msixRoot "app\Assets") | Out-Null
  Copy-Item (Join-Path $PSScriptRoot "msix\Assets\*") (Join-Path $msixRoot "app\Assets") -Force
  # 雛形は Package.appxmanifest（tests/msix-pack/pack.sh と同じ置換）。Identity は Partner Center 確定後に環境変数で差し替える
  $manifest = Get-Content (Join-Path $PSScriptRoot "msix\Package.appxmanifest") -Raw
  $identityName = if ($env:MSIX_IDENTITY_NAME) { $env:MSIX_IDENTITY_NAME } else { "JapanMarketing.ReplyFive" }
  $publisherCn = if ($env:MSIX_PUBLISHER_CN) { $env:MSIX_PUBLISHER_CN } else { "CN=REPLACE-PUBLISHER" }
  $publisherDisplay = if ($env:MSIX_PUBLISHER_DISPLAY) { $env:MSIX_PUBLISHER_DISPLAY } else { "JapanMarketing LLC" }
  $manifest = $manifest -replace "REPLACE_PACKAGE_IDENTITY_NAME", $identityName -replace "REPLACE_PUBLISHER_CN", $publisherCn -replace "REPLACE_PUBLISHER_DISPLAY_NAME", $publisherDisplay
  $manifest = $manifest -replace 'Version="0\.8\.0\.0"', "Version=`"$Version.0`"" -replace 'ProcessorArchitecture="[a-z0-9]*"', "ProcessorArchitecture=`"$Arch`""
  Set-Content (Join-Path $msixRoot "app\AppxManifest.xml") $manifest -Encoding UTF8
  $makeappx = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1).FullName
  & $makeappx pack /d (Join-Path $msixRoot "app") /p (Join-Path $dist "ReplyFive-$Version$suffix.msix") /o
  if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
}
Write-Host "== done: $dist"
