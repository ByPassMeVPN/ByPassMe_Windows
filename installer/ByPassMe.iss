; ByPassMe Windows Installer (Inno Setup 6)
; Сборка: ISCC.exe /DAppVersion=1.3.1 /DPublishDir=..\publish\Release installer/ByPassMe.iss

#ifndef AppVersion
  #define AppVersion "1.3.1"
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
DisableDirPage=yes
PrivilegesRequired=admin
CloseApplications=force
SetupMutex=ByPassMeSetupMutex
UsePreviousAppDir=no
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
VersionInfoDescription=ByPassMe — VPN + обход Б/С
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
Name: "{group}\ByPassMe"; Filename: "{app}\ByPassMe.exe"; Comment: "ByPassMe — VPN и обход"
Name: "{group}\Удалить ByPassMe"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ByPassMe"; Filename: "{app}\ByPassMe.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\FlyFrogLLC\ByPassMe"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
; Всегда запускать ByPassMe.exe с повышением (щит UAC на ярлыке)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\ByPassMe.exe"; ValueData: "~ RUNASADMIN"; Flags: uninsdeletevalue

[Run]
; runascurrentuser — от текущего админа установщика, иначе код 740 (requireAdministrator)
Filename: "{app}\ByPassMe.exe"; Description: "Запустить ByPassMe"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
Filename: "{app}\tools\ByPassMe.TunnelHelper.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
; settings.json в LocalAppData не трогаем — сессия переживает удаление/переустановку (как раньше).
Type: filesandordirs; Name: "{commonappdata}\ByPassMe"

[Messages]
russian.WelcomeLabel2=Установка [name/ver].%n%nУстановщик сразу запросит права администратора. После установки ByPassMe тоже запускается от администратора (нужно для VPN-туннеля).%n%nПрограмма будет обновлена; ваши настройки и подписка сохранятся. Xray и WireGuard уже внутри — ничего скачивать не нужно.
russian.FinishedLabel=Программа [name] установлена.%n%nНажмите «Запустить ByPassMe». Если подписка уже сохранялась на этом ПК — приложение откроется сразу; иначе вставьте ссылку из бота.

[Code]
procedure KillImage(const ImageName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ImageName + ' /T',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure ScCmd(const Args: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure WipeDir(const Dir: String);
begin
  if (Dir <> '') and DirExists(Dir) then
    DelTree(Dir, True, True, True);
end;

function UninstallRegKey(): String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1';
end;

function GetUninstallString(): String;
var
  UninstallString: String;
begin
  UninstallString := '';
  if not RegQueryStringValue(HKLM, UninstallRegKey(), 'UninstallString', UninstallString) then
    RegQueryStringValue(HKCU, UninstallRegKey(), 'UninstallString', UninstallString);
  Result := UninstallString;
end;

procedure WipeAllByPassMe();
var
  UninstallString: String;
  ResultCode: Integer;
  InstallPath: String;
begin
  Log('Полная очистка предыдущей установки ByPassMe');

  KillImage('ByPassMe.exe');
  KillImage('xray.exe');
  KillImage('sing-box.exe');
  KillImage('bypassclient.exe');
  KillImage('ByPassMe.TunnelHelper.exe');
  KillImage('wireguard.exe');
  Sleep(400);

  ScCmd('stop "ByPassMeTunnelHelper"');
  ScCmd('delete "ByPassMeTunnelHelper"');
  ScCmd('stop "WireGuardTunnel$ByPassMe-Bypass"');
  ScCmd('delete "WireGuardTunnel$ByPassMe-Bypass"');
  ScCmd('stop "WireGuardTunnel$wg-bypass"');
  ScCmd('delete "WireGuardTunnel$wg-bypass"');

  UninstallString := GetUninstallString();
  if UninstallString <> '' then
  begin
    UninstallString := RemoveQuotes(UninstallString);
    Log('Тихое удаление: ' + UninstallString);
    Exec(UninstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(600);
    KillImage('ByPassMe.exe');
    KillImage('xray.exe');
    KillImage('sing-box.exe');
    KillImage('bypassclient.exe');
    KillImage('ByPassMe.TunnelHelper.exe');
  end;

  InstallPath := '';
  RegQueryStringValue(HKLM, 'SOFTWARE\FlyFrogLLC\ByPassMe', 'InstallPath', InstallPath);
  if InstallPath <> '' then
    WipeDir(InstallPath);

  WipeDir(ExpandConstant('{pf}\ByPassMe'));
  WipeDir(ExpandConstant('{pf32}\ByPassMe'));
  WipeDir(ExpandConstant('{commonappdata}\ByPassMe'));
  { LocalAppData\ByPassMe (settings.json) — не удаляем: сессия остаётся после переустановки }

  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\FlyFrogLLC\ByPassMe');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\FlyFrogLLC');
  RegDeleteKeyIncludingSubkeys(HKLM, UninstallRegKey());
  RegDeleteKeyIncludingSubkeys(HKCU, UninstallRegKey());
  RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
    ExpandConstant('{pf}\ByPassMe\ByPassMe.exe'));

  Sleep(400);
end;

function InitializeSetup(): Boolean;
begin
  WipeAllByPassMe();
  Result := True;
end;

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
        'Проверьте лог: C:\ProgramData\ByPassMe\service-install.log',
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
  if CurStep = ssInstall then
    WipeAllByPassMe();
  if CurStep = ssPostInstall then
    InstallVpnService();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  KillImage('ByPassMe.exe');
  KillImage('xray.exe');
  KillImage('sing-box.exe');
  KillImage('bypassclient.exe');
  KillImage('wireguard.exe');

  ScCmd('stop "ByPassMeTunnelHelper"');
  ScCmd('delete "ByPassMeTunnelHelper"');
  ScCmd('stop "WireGuardTunnel$ByPassMe-Bypass"');
  ScCmd('delete "WireGuardTunnel$ByPassMe-Bypass"');

  { Не чистим LocalAppData — settings.json остаётся для следующей установки }
  WipeDir(ExpandConstant('{commonappdata}\ByPassMe'));
  WipeDir(ExpandConstant('{pf}\ByPassMe'));
end;
