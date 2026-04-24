#define AppName "WheelSwitcher"
#define AppVersion GetEnv("APP_VERSION")
#define AppExe "wheel_switcher.exe"

[Setup]
AppId={{8F3A9E42-1B2C-4D5E-9A1F-7E3F8C6D4A1B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Michal Porubcin
AppPublisherURL=https://github.com/mpf11/wheel_switcher
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=dist
OutputBaseFilename=wheel_switcher-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

[Files]
Source: "..\target\x86_64-pc-windows-msvc\release\wheel_switcher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WheelSwitcher"
