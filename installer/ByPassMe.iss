; ByPassMe Windows Installer (Inno Setup 6)
; Сборка: ISCC.exe /DAppVersion=1.1.32 /DPublishDir=..\publish\Release installer\ByPassMe.iss

#ifndef AppVersion
  #define AppVersion "1.1.32"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\Release"
#endif

[Setup]
AppId={{B7E4A1C2-9F3D-4E8B-A5C6-1D2E3F4A5B6C}
AppName=ByPassMe
AppVersion={#AppVersion}
AppVerName=ByPassMe {#AppVersion}
AppPublisher=FlyFrogLLC
AppPublisherURL=https://bypassme.online
AppSupportURL=https://bypassme.online
AppUpdatesURL=https://bypassme.online
DefaultDirName={pf}\ByPassMe
DefaultGroupName=ByPassMe
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=ByPassMe-Setup-{#AppVersion}
SetupIconFile=..\ByPassMe\Assets\ByPassMe.ico
UninstallDisplayIcon={app}\ByPassMe.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=FlyFrogLLC
VersionInfoDescription=ByPassMe — обход Б/С
VersionInfoProductName=ByPassMe
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[Dirs]
Name: "{commonappdata}\ByPassMe\tunnels"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ByPassMe"; Filename: "{app}\ByPassMe.exe"; Comment: "ByPassMe — обход Б/С"
Name: "{group}\Удалить ByPassMe"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ByPassMe"; Filename: "{app}\ByPassMe.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\FlyFrogLLC\ByPassMe"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\ByPassMe.exe"; Description: "Запустить ByPassMe"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\tools\ByPassMe.TunnelHelper.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
; Настройки, логи, кэш пользователя (подписка, VK, uuid)
Type: filesandordirs; Name: "{userpf}\AppData\Local\ByPassMe"
Type: filesandordirs; Name: "{userpf}\AppData\Local\FlyFrogLLC\ByPassMe"
Type: dirifempty; Name: "{userpf}\AppData\Local\FlyFrogLLC"
; WireGuard-конфиги и служебные логи (ProgramData)
Type: filesandordirs; Name: "{commonappdata}\ByPassMe"

[Messages]
russian.WelcomeLabel2=Установка [name/ver] в Program Files.%n%nОдин раз потребуются права администратора — после этого VPN работает в фоне без лишних окон.%n%nWireGuard включён в установщик.
russian.FinishedLabel=Программа [name] установлена.%n%nЗапустите ByPassMe, вставьте ссылку подписки и подключитесь.

[Code]
function InstallVpnService(): Boolean;
var
  ResultCode: Integer;
  HelperPath: String;
begin
  HelperPath := ExpandConstant('{app}\tools\ByPassMe.TunnelHelper.exe');
  if not FileExists(HelperPath) then
  begin
    MsgBox('Не найден VPN-помощник:'#13#10 + HelperPath, mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Exec(HelperPath, '--install-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
      Result := True
    else
    begin
      MsgBox(
        'Не удалось установить VPN-службу ByPassMeTunnelHelper (код ' + IntToStr(ResultCode) + ').'#13#10#13#10 +
        'Приложение установлено, но VPN может не работать.'#13#10 +
        'Проверьте лог: C:\ProgramData\ByPassMe\service-install.log'#13#10#13#10 +
        'Попробуйте запустить от администратора:'#13#10 +
        HelperPath + ' --install-service',
        mbError, MB_OK);
      Result := False;
    end;
  end
  else
  begin
    MsgBox('Не удалось запустить установку VPN-службы.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallVpnService();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LocalDir, LegacyDir, LegacyRoot, CommonDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  LocalDir := ExpandConstant('{userpf}\AppData\Local\ByPassMe');
  LegacyDir := ExpandConstant('{userpf}\AppData\Local\FlyFrogLLC\ByPassMe');
  LegacyRoot := ExpandConstant('{userpf}\AppData\Local\FlyFrogLLC');
  CommonDir := ExpandConstant('{commonappdata}\ByPassMe');

  if DirExists(LocalDir) then
    DelTree(LocalDir, True, True, True);
  if DirExists(LegacyDir) then
    DelTree(LegacyDir, True, True, True);
  if DirExists(LegacyRoot) then
    RemoveDir(LegacyRoot);
  if DirExists(CommonDir) then
    DelTree(CommonDir, True, True, True);
end;
