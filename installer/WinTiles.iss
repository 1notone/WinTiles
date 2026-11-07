; WinTiles installer (Inno Setup 6). Built by build.ps1:
;   ISCC.exe installer\WinTiles.iss /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=..\artifacts\publish\x64
;
; Installs per user (no admin prompt) to %LOCALAPPDATA%\Programs\WinTiles, adds a Start menu
; shortcut, optionally starts WinTiles at sign-in, and opens it when setup finishes.

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#define AppName "WinTiles"
#define AppExe "WinTiles.exe"
; WinTiles was called Tessera before 1.2.0; setup removes that version (keeping its tiles and settings,
; which WinTiles moves across the first time it runs).
#define OldAppId "{5E7C1B2A-9F4D-4C3B-A1E8-7D2F0B6C9A41}"

[Setup]
AppId={{025FDF92-040D-4ADC-B7B5-C40E8A030B50}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WinTiles contributors
VersionInfoVersion={#NumericVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=WinTiles-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\WinTiles\Assets\WinTiles.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2/ultra64
SolidCompression=yes
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nWinTiles brings the Windows 8 Start screen to Windows 11: colorful tiles, named groups and Live Tiles that show the time, weather, your next meeting, headlines and more.%n%nIf WinTiles (or Tessera, its earlier name) is already installed, it will be closed and updated. Your tiles and settings are kept.
FinishedLabel=WinTiles is installed.%n%nPress Ctrl+Alt+Space (or click the WinTiles icon in the system tray) to bring Start to the front. Just start typing to search your apps.

[Tasks]
Name: "startup"; Description: "Start WinTiles when I sign in"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The app's own Settings → "Start WinTiles when I sign in" edits this same value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open WinTiles now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopWinTiles"

[Code]
var
  DeleteData: Boolean;

{ Tessera (WinTiles' name before 1.2.0) had its own uninstaller entry; run it quietly first.
  Silent, it leaves %APPDATA%\Tessera alone, and WinTiles moves those settings across. }
procedure RemoveTessera();
var
  Key, Uninstaller: String;
  Code: Integer;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#OldAppId}_is1';
  if RegQueryStringValue(HKEY_CURRENT_USER, Key, 'UninstallString', Uninstaller) then
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemoveTessera();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Code: Integer;
begin
  DataDir := ExpandConstant('{userappdata}\{#AppName}');
  if CurUninstallStep = usUninstall then
  begin
    { Ask before the program files go, so WinTiles can still delete its saved service keys. }
    DeleteData := DirExists(DataDir) and not UninstallSilent and
      (MsgBox('Also delete your WinTiles tiles, settings and saved service keys?' + #13#10 + #13#10 + DataDir,
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
    if DeleteData then
      Exec(ExpandConstant('{app}\{#AppExe}'), '--forget-secrets', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { Settings → "Start WinTiles when I sign in" may have set this even if the installer task wasn't chosen. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    if DeleteData then DelTree(DataDir, True, True, True);
  end;
end;
