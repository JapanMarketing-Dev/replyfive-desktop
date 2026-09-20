; ReplyFive for Windows — Inno Setup 6 スクリプト（直接配布用の署名付きインストーラ）。
; ストア配布（Microsoft Store）は packaging/windows/msix を使う。
; 使い方（Windows）: packaging/windows/build.ps1 が publish → このスクリプト → signtool の順に実行する。
#ifndef AppVersion
  #define AppVersion "0.8.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\dist\win-x64"
#endif

[Setup]
AppId={{7D3F6C5A-1B7E-4B2E-9C9E-5A8E0A1F5E11}
AppName=ReplyFive
AppVersion={#AppVersion}
AppVerName=ReplyFive {#AppVersion}
AppPublisher=JapanMarketing LLC
AppPublisherURL=https://replyfive.app
AppSupportURL=https://replyfive.app
AppUpdatesURL=https://replyfive.app/download/windows
; 利用者ごとのインストール（管理者権限なし）。アプリ内アップデートが同じ場所へ上書きできる
DefaultDirName={localappdata}\Programs\ReplyFive
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
OutputDir=..\..\dist
#ifndef Arch
#define Arch "x64"
#endif
#if Arch == "x64"
OutputBaseFilename=ReplyFive-Setup
#else
OutputBaseFilename=ReplyFive-Setup-{#Arch}
#endif
SetupIconFile=..\..\src\ReplyFive.Desktop\Assets\replyfive.ico
UninstallDisplayIcon={app}\ReplyFive.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=JapanMarketing LLC
VersionInfoDescription=ReplyFive Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "startup"; Description: "{cm:AutoStartProgram,ReplyFive}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ReplyFive"; Filename: "{app}\ReplyFive.exe"
Name: "{userstartup}\ReplyFive"; Filename: "{app}\ReplyFive.exe"; Tasks: startup

[Registry]
; replyfive:// の URL スキーム（サインイン後にブラウザからアプリへ戻る。付録BF）
Root: HKCU; Subkey: "Software\Classes\replyfive"; ValueType: string; ValueName: ""; ValueData: "URL:ReplyFive"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\replyfive"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\replyfive\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\ReplyFive.exe"",0"
Root: HKCU; Subkey: "Software\Classes\replyfive\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ReplyFive.exe"" ""%1"""

[Run]
; インストール後に起動（アプリ内アップデートの /VERYSILENT でも同じ経路で再起動する）
Filename: "{app}\ReplyFive.exe"; Description: "{cm:LaunchProgram,ReplyFive}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ReplyFive.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM ReplyFive.exe /F"; Flags: runhidden; RunOnceId: "KillReplyFive"
