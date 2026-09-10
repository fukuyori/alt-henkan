; Alt Henkan Inno Setup Script
;
; Normally invoked from scripts\build-installer.ps1, which passes these definitions:
;   /DMyAppVersion=<version>  <Version> from src\AltHenkan\AltHenkan.csproj (defaults to 0.0.0)
;   /DSIGN                    enable signing (SignTool / SignedUninstaller)
;   /Salthenkansign=<command>       sign command ($f = file to sign, $q = double quote)
;   /DNOUIACCESS              the exe has no uiAccess manifest: install per-user without
;                             administrator rights. By default the exe carries a uiAccess manifest
;                             and must live under Program Files (a secure location), so the
;                             installer is per-machine and requires administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Alt Henkan"
#define MyAppFileName "AltHenkan"
#define MyAppPublisher "fukuyori"
#define MyAppExeName "AltHenkan.exe"
#define MyAppDescription "Tap left Alt for IME off and right Alt for IME on"
#define MyPublishDir "src\AltHenkan\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{109FAC38-A24D-443F-A1D6-DC56D27BCCFF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppDescription}
DefaultDirName={autopf}\{#MyAppFileName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename={#MyAppFileName}_Setup_{#MyAppVersion}
SetupIconFile=src\AltHenkan\AltHenkan.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#ifdef NOUIACCESS
PrivilegesRequired=lowest
#else
; uiAccess executables only start from a secure location such as {commonpf}, so install per-machine.
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close a running AltHenkan.exe via Restart Manager before overwriting it.
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
#ifdef SIGN
SignTool=althenkansign
SignedUninstaller=yes
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} automatically at sign-in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Startup registers the HKCU\...\Run value "AltHenkan". Removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppFileName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup
; Remove the value when reinstalling with the startup task unchecked.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#MyAppFileName}"; Flags: deletevalue; Tasks: not startup

[Run]
; shellexec: a uiAccess executable cannot be started with CreateProcess (error 740,
; ERROR_ELEVATION_REQUIRED); it must go through ShellExecute so AppInfo can launch it.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
// Stop a resident AltHenkan.exe: ask it to close (WM_CLOSE via taskkill), give it a moment,
// then force-kill anything that is still running. Restart Manager alone is not enough
// because Alt Henkan has no visible main window.
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;
