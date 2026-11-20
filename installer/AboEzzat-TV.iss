#define MyAppName "AboEzzat TV"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AboEzzat"
#define MyAppExeName "AboEzzatReceiver.Windows.exe"

[Setup]
AppId={{B7D94B2D-8E43-4E58-9C0B-AB0E22A77101}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\\AboEzzat TV
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\\{#MyAppExeName}
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
Source: "..\\windows\\publish\\win-x64\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\AboEzzat TV"; Filename: "{app}\\{#MyAppExeName}"
Name: "{autodesktop}\\AboEzzat TV"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{sys}\\netsh.exe"; Parameters: "advfirewall firewall add rule name=""AboEzzat TV Receiver"" dir=in action=allow protocol=TCP localport=8081 profile=private"; Flags: runhidden waituntilterminated
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch AboEzzat TV"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AboEzzat TV Receiver"""; Flags: runhidden waituntilterminated

[Code]
const
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2DownloadPage = 'https://developer.microsoft.com/microsoft-edge/webview2/';

function IsWebView2RuntimeInstalled: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM,
      'SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\' + WebView2ClientGuid,
      'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'))
    or
    (RegQueryStringValue(HKCU,
      'Software\\Microsoft\\EdgeUpdate\\Clients\\' + WebView2ClientGuid,
      'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsWebView2RuntimeInstalled then
  begin
    if MsgBox(
      'Microsoft Edge WebView2 Runtime is not detected. AboEzzat TV can still be installed, but its built-in browser will not work until this Microsoft component is installed.' + #13#10 + #13#10 +
      'Open the official Microsoft WebView2 Runtime download page now?',
      mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', WebView2DownloadPage, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
