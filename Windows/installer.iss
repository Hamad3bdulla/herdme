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
CloseApplications=yes
CloseApplicationsFilter=HerdMe.Windows.exe
RestartApplications=no
AppMutex=Local\HerdMe.Desktop.SingleInstance
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HerdMe"; Filename: "{app}\HerdMe.Windows.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\HerdMe"; Filename: "{app}\HerdMe.Windows.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The app owns this opt-in value. Setup only registers uninstall cleanup.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HerdMe"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\HerdMe.Windows.exe"; Description: "Launch HerdMe"; Flags: nowait postinstall skipifsilent
