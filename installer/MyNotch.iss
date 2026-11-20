; Inno Setup 6 installer script for MyNotch (per-user x64 install)
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppTag
  #define MyAppTag "dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8E5F2D6B-9B8E-4E2A-B4E5-1F7C8C3F4D91}
AppName=MyNotch
AppVersion={#MyAppVersion}
AppVerName=MyNotch {#MyAppTag}
AppPublisher=MyNotch
AppPublisherURL=https://github.com/brendatamaar/my-notch
AppSupportURL=https://github.com/brendatamaar/my-notch/issues
AppUpdatesURL=https://github.com/brendatamaar/my-notch/releases
DefaultDirName={localappdata}\Programs\MyNotch
DefaultGroupName=MyNotch
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=MyNotch-Setup-{#MyAppTag}-win-x64
SetupIconFile=..\MyNotch\Assets\MyNotch.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\MyNotch.Instance
CloseApplications=yes
CloseApplicationsFilter=MyNotch.exe
UninstallDisplayIcon={app}\MyNotch.exe
UninstallDisplayName=MyNotch

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start MyNotch when Windows starts"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MyNotch"; Filename: "{app}\MyNotch.exe"
Name: "{autodesktop}\MyNotch"; Filename: "{app}\MyNotch.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyNotch"; ValueData: """{app}\MyNotch.exe"""; Flags: uninsdeletevalue; Tasks: startup

[UninstallDelete]
Type: filesandordirs; Name: "{app}\NotificationIdentity"

[Run]
Filename: "{app}\MyNotch.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,MyNotch}"; Flags: nowait postinstall skipifsilent shellexec
Filename: "{app}\MyNotch.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent shellexec; Check: ShouldAutoLaunch

[Code]
function ShouldAutoLaunch: Boolean;
begin
  Result := ExpandConstant('{param:LAUNCH|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MyNotch');
  end;
end;
