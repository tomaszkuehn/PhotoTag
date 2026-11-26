; Instalator PhotoTag (Inno Setup 6) — Windows 10/11 x64.
; Budowanie: .\installer\build-installer.ps1 (publikuje program i kompiluje ten skrypt).
; Wersja domyślnie z PhotoTag.exe; można nadpisać: ISCC /DAppVersion=1.2.3 PhotoTag.iss

#define AppName "PhotoTag"
#define AppExe "PhotoTag.exe"
#define AppPublisher "Tomasz Kuehn"
#define PublishDir "..\publish"
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExe)
#endif

[Setup]
; Stały identyfikator — nie zmieniać, inaczej aktualizacje nie zastąpią poprzedniej wersji.
AppId={{6F3C2A4E-8B1D-4E7A-9C55-2D8E1F0B7A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\Assets\PhotoTag.ico
OutputDir=output
OutputBaseFilename=PhotoTag-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 10/11, tylko 64-bit
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; domyślnie dla wszystkich (Program Files); użytkownik bez uprawnień admina może wybrać instalację tylko dla siebie
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
#ifdef Sign
; podpis instalatora i deinstalatora — narzędzie „signtool” przekazuje build-installer.ps1 (/S…)
SignTool=signtool
SignedUninstaller=yes
#endif
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#PublishDir}\PhotoTag-poradnik.pdf"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} — poradnik"; Filename: "{app}\PhotoTag-poradnik.pdf"; Check: FileExists(ExpandConstant('{app}\PhotoTag-poradnik.pdf'))
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Ustawienia użytkownika (%APPDATA%\PhotoTag, %LOCALAPPDATA%\PhotoTag) i pliki opisy.json
; przy zdjęciach zostają po odinstalowaniu.

[Code]
const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function HasWebView2Key(RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
begin
  Result := HasWebView2Key(HKLM32, WebView2ClientKey) or
            HasWebView2Key(HKLM64, WebView2ClientKey) or
            HasWebView2Key(HKCU, WebView2ClientKey);
end;

{ Mapa działa w WebView2. W Windows 11 i zaktualizowanym Windows 10 runtime już jest;
  gdy go brak, pobieramy oficjalny instalator Microsoft (Evergreen Bootstrapper). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if WebView2Installed then Exit;

  if MsgBox('Do wyświetlania mapy potrzebny jest Microsoft Edge WebView2 Runtime, którego nie znaleziono.' + #13#10#13#10 +
            'Pobrać i zainstalować go teraz (ok. 2 MB, wymaga internetu)?',
            mbConfirmation, MB_YESNO) <> IDYES then
  begin
    MsgBox('PhotoTag zostanie zainstalowany, ale mapa nie zadziała do czasu instalacji WebView2 Runtime ' +
           '(https://developer.microsoft.com/microsoft-edge/webview2/).', mbInformation, MB_OK);
    Exit;
  end;

  try
    DownloadTemporaryFile(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
                SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('Instalacja WebView2 Runtime nie powiodła się (kod ' + IntToStr(ResultCode) + '). ' +
             'Można ją powtórzyć ręcznie później.', mbError, MB_OK);
  except
    MsgBox('Nie udało się pobrać WebView2 Runtime: ' + GetExceptionMessage + #13#10 +
           'Można go zainstalować ręcznie później.', mbError, MB_OK);
  end;
end;
