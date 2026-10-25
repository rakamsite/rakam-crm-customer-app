#define AppVersion "0.2.2"
#define AppExe "Rakam CRM.exe"

[Setup]
AppId={{11D1EF98-B017-4771-88E6-3AD21C3E97C8}
AppName=Rakam CRM
AppVersion={#AppVersion}
AppPublisher=Rakam
DefaultDirName={localappdata}\Programs\Rakam\CustomerApp
DefaultGroupName=Rakam CRM
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=Rakam CRM Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=Rakam.CustomerApp.exe,Rakam CRM.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupLogging=yes
SetupIconFile=..\src\Rakam.CustomerApp.Windows\Assets\rakam.ico

[Files]
Source: "..\dist\Rakam-CRM\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\Rakam CRM"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0
Name: "{userprograms}\Rakam CRM"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0

[InstallDelete]
Type: files; Name: "{userdesktop}\ثبت مراجعان راکام.lnk"
Type: files; Name: "{userprograms}\ثبت مراجعان راکام.lnk"
Type: files; Name: "{app}\Rakam.CustomerApp.exe"
Type: files; Name: "{app}\Rakam.CustomerApp.dll"
Type: files; Name: "{app}\Rakam.CustomerApp.deps.json"
Type: files; Name: "{app}\Rakam.CustomerApp.runtimeconfig.json"
Type: files; Name: "{app}\Rakam.CustomerApp.pdb"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Rakam CRM"; Flags: nowait postinstall skipifsilent

