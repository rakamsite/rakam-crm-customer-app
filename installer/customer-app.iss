#define AppVersion "0.2.0"
#define AppExe "Rakam.CustomerApp.exe"

[Setup]
AppId={{11D1EF98-B017-4771-88E6-3AD21C3E97C8}
AppName=ثبت مراجعان راکام
AppVersion={#AppVersion}
AppPublisher=Rakam
DefaultDirName={localappdata}\Programs\Rakam\CustomerApp
DefaultGroupName=ثبت مراجعان راکام
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=Rakam-Customer-App-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=Rakam.CustomerApp.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupLogging=yes

[Files]
Source: "..\dist\Rakam-Customer-App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\ثبت مراجعان راکام"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userprograms}\ثبت مراجعان راکام"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Rakam Customer App"; Flags: nowait postinstall skipifsilent

