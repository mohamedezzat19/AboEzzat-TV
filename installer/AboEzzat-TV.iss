#define MyAppName "AboEzzat TV"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AboEzzat"
#define MyAppExeName "AboEzzatReceiver.Windows.exe"

[Setup]
AppId={{B7D94B2D-8E43-4E58-9C0B-AB0EZZATTV01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\AboEzzat TV
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
OutputBaseFilename=AboEzzat-TV-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\windows\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AboEzzat TV"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\AboEzzat TV"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "http add urlacl url=http://+:8081/ user=S-1-5-32-545"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""AboEzzat TV Receiver"" dir=in action=allow protocol=TCP localport=8081 profile=private"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch AboEzzat TV"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AboEzzat TV Receiver"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "http delete urlacl url=http://+:8081/"; Flags: runhidden waituntilterminated
