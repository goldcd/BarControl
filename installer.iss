; Inno Setup Script for BarControl (Soundbar & HDMI-CEC Control)
; Builds a standard Windows Setup Wizard (e.g. BarControl-Setup.exe)

#define MyAppName "BarControl"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "goldcd"
#define MyAppURL "https://github.com/goldcd/BarControl"
#define MyAppExeName "BarControl.exe"

[Setup]
AppId={{9A4D2F6E-3B1C-4E8A-B72D-5C9F1A8E3D4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=BarControl-Setup-v{#MyAppVersion}
SetupIconFile=Assets\app.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Launch BarControl automatically on Windows startup"; GroupDescription: "Windows Integration:"

[Files]
Source: "dist\BarControl.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Tasks: startupicon; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
