; Inno Setup script for AppLock. Build with build-installer.ps1 (it publishes the app first).

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{8C6E3F0A-5B1D-4E7A-9F21-3A7C2B9D4E61}
AppName=AppLock
AppVersion={#AppVersion}
AppVerName=AppLock {#AppVersion}
AppPublisher=AppLock
; AppLock expects to live in Program Files\AppLock, so the folder isn't changeable.
DefaultDirName={autopf}\AppLock
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=AppLock-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\AppLock.exe
UninstallDisplayName=AppLock

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "build\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\AppLock"; Filename: "{app}\AppLock.exe"
Name: "{autodesktop}\AppLock"; Filename: "{app}\AppLock.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AppLock.exe"; Parameters: "--install-service"; Flags: runhidden waituntilterminated; StatusMsg: "Starting the AppLock service..."
Filename: "{app}\AppLock.exe"; Description: "Open AppLock now"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
; Removes the background service and every app lock, so no app is left pointing at a deleted AppLock.
Filename: "{app}\AppLock.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLocks"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\AppLock"

[Code]
// Stop the running service before files are replaced during an upgrade.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop AppLockSvc', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(2000);
  Result := '';
end;
