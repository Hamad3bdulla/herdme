#ifndef MyAppVersion
  #error MyAppVersion is required
#endif
#ifndef MyAppBuild
  #error MyAppBuild is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
AppId={{6C053A4F-1FF3-4B94-A6DF-17CAF32FAC5F}
AppName=HerdMe
AppVersion={#MyAppVersion}
AppVerName=HerdMe {#MyAppVersion}
AppPublisher=HerdMe contributors
AppPublisherURL=https://github.com/Hamad3bdulla/herdme
AppSupportURL=https://github.com/Hamad3bdulla/herdme/issues
AppUpdatesURL=https://github.com/Hamad3bdulla/herdme/releases
VersionInfoVersion={#MyAppVersion}.{#MyAppBuild}
VersionInfoCompany=HerdMe contributors
VersionInfoDescription=HerdMe installer
VersionInfoProductName=HerdMe
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\HerdMe
DefaultGroupName=HerdMe
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=HerdMe.Windows\Assets\HerdMe.ico
UninstallDisplayIcon={app}\HerdMe.Windows.exe
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
#if Ver < EncodeVer(6, 6, 0)
WizardResizable=no
#endif
WizardSizePercent=100
CloseApplications=yes
CloseApplicationsFilter=HerdMe.Windows.exe,herdme.exe
RestartApplications=no
; PrepareToInstall closes older versions which otherwise only hide on WM_CLOSE.
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "stop-for-update.ps1"; Flags: dontcopy
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HerdMe"; Filename: "{app}\HerdMe.Windows.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\HerdMe"; Filename: "{app}\HerdMe.Windows.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The app owns this opt-in value. Setup only registers uninstall cleanup.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HerdMe"; Flags: dontcreatekey uninsdeletevalue
; Opt-in shell integration written by the app (Settings > General). Setup never creates
; these keys; it only removes them on uninstall.
Root: HKCU; Subkey: "Software\Classes\herdme"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\HerdMe.Link"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\HerdMe.Link"; Flags: dontcreatekey uninsdeletekey

[UninstallDelete]
Type: files; Name: "{userstartup}\HerdMe.lnk"
Type: files; Name: "{localappdata}\HerdMe\bin\herdme.cmd"
Type: filesandordirs; Name: "{localappdata}\Microsoft\Windows Terminal\Fragments\HerdMe"

[Run]
Filename: "{app}\HerdMe.Windows.exe"; Description: "Launch HerdMe"; Flags: nowait postinstall skipifsilent
; "Restart to update" in the app runs setup silently with /RELAUNCH=1 and expects HerdMe back.
Filename: "{app}\HerdMe.Windows.exe"; Flags: nowait skipifnotsilent; Check: RelaunchRequested

[UninstallRun]
; Removes the opt-in Windows notification registration (General > Buttons on notifications).
; It does nothing when that switch was never turned on.
Filename: "{app}\HerdMe.Windows.exe"; Parameters: "--unregister-notifications"; Flags: runhidden waituntilterminated; RunOnceId: "HerdMeUnregisterNotifications"
; Removes the opt-in Windows Defender exclusion (General > Speed up PHP). It asks for
; administrator approval only when that switch was turned on; otherwise it does nothing.
Filename: "{app}\HerdMe.Windows.exe"; Parameters: "--remove-defender-exclusion"; Flags: runhidden waituntilterminated; RunOnceId: "HerdMeRemoveDefenderExclusion"

[Code]
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Result := '';
  ExtractTemporaryFile('stop-for-update.ps1');
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{tmp}\stop-for-update.ps1')) +
    ' -InstallDirectory ' + AddQuotes(ExpandConstant('{app}'));
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not stop HerdMe. Close HerdMe and retry the update.'
  else if ResultCode <> 0 then
    Result := 'Some HerdMe services are still running. Close them and retry the update.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  MarkerPath: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  MarkerPath := ExpandConstant('{localappdata}\HerdMe\Config\onboarding-after-reinstall.flag');
  if ForceDirectories(ExtractFileDir(MarkerPath)) then
    SaveStringToFile(MarkerPath, 'show-onboarding', False);
end;
