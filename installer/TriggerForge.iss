#ifndef AppVersion
  #error Pass AppVersion from build/publish.ps1.
#endif

[Setup]
AppId={{D11C945E-623E-42EB-B0DD-3D38E2711163}
AppName=TriggerForge
AppVersion={#AppVersion}
AppPublisher=KH
DefaultDirName={autopf}\TriggerForge
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
RestartApplications=no
UninstallDisplayIcon={app}\TriggerForge.exe
OutputDir=..\artifacts
OutputBaseFilename=TriggerForge-{#AppVersion}-setup
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
#ifdef SignRelease
SignTool=TriggerForgeSign
SignedUninstaller=yes
SignedUninstallerDir=..\artifacts
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.DesktopIcon=Create a desktop shortcut
tr.DesktopIcon=Masaüstü kısayolu oluştur
en.RestoreShortcut=TriggerForge — Restore controller
tr.RestoreShortcut=TriggerForge — Kontrolcüyü geri getir
en.MissingWebView=TriggerForge requires the Microsoft Edge WebView2 Runtime. Download it from https://developer.microsoft.com/microsoft-edge/webview2/. Setup will continue.
tr.MissingWebView=TriggerForge için Microsoft Edge WebView2 Runtime gerekiyor. https://developer.microsoft.com/microsoft-edge/webview2/ adresinden indirebilirsiniz. Kurulum devam edecek.
en.RestoreFailed=Controller recovery failed or timed out. Run TriggerForge — Restore controller from Start, then try uninstalling again.
tr.RestoreFailed=Kontrolcü geri getirilemedi veya işlem zaman aşımına uğradı. Başlat menüsünden TriggerForge — Kontrolcüyü geri getir seçeneğini çalıştırıp kaldırmayı tekrar deneyin.
en.AppRunning=TriggerForge is running. Close it from its tray menu, then click OK. Cancel stops.
tr.AppRunning=TriggerForge çalışıyor. Tepsi menüsünden kapatıp Tamam'a basın. İptal işlemi durdurur.
en.RestoreFailedAsk=Controller recovery failed or timed out. The controller may stay hidden from other apps until HidHide's list is cleared.%n%nUninstall anyway?
tr.RestoreFailedAsk=Kontrolcü geri getirilemedi veya işlem zaman aşımına uğradı. HidHide listesi temizlenene kadar kontrolcü diğer uygulamalardan gizli kalabilir.%n%nYine de kaldırılsın mı?
en.RemoveData=Also remove settings and profiles? Local logs and WebView2 data will also be deleted for this Windows account.
tr.RemoveData=Ayarlar ve profiller de silinsin mi? Bu Windows hesabının yerel günlükleri ve WebView2 verileri de silinecek.
en.RemoveDrivers=Also remove the drivers usbip-win2 and HidHide? Other apps may use these drivers.
tr.RemoveDrivers=usbip-win2 ve HidHide sürücüleri de kaldırılsın mı? Başka uygulamalar bu sürücüleri kullanıyor olabilir.
en.DriverRemovalFailed=Could not uninstall %1. You can remove it from Windows Settings.
tr.DriverRemovalFailed=%1 kaldırılamadı. Windows Ayarları üzerinden kaldırabilirsiniz.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commonprograms}\TriggerForge"; Filename: "{app}\TriggerForge.exe"; WorkingDir: "{app}"
Name: "{commonprograms}\{cm:RestoreShortcut}"; Filename: "{app}\TriggerForge.exe"; Parameters: "--restore"; WorkingDir: "{app}"
Name: "{commondesktop}\TriggerForge"; Filename: "{app}\TriggerForge.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TriggerForge.exe"; Description: "{cm:LaunchProgram,TriggerForge}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; After an update the app passes /RELAUNCH; it starts again as the signed-in user, not elevated.
Filename: "{app}\TriggerForge.exe"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RuntimeKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  UsbIpKey = '{199505b0-b93d-4521-a8c7-897818e0205a}_is1';

var
  DriverRestartNeeded: Boolean;

const
  AppMutexName = 'Local\TriggerForge.Instance';

{ The app exits right after starting an update, so a silent update first waits for it.
  Interactive setups and uninstalls ask the user to close it instead. }
function WaitForApp(Silent: Boolean): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  Waited := 0;
  while CheckForMutexes(AppMutexName) do begin
    if Silent then begin
      if Waited >= 30000 then begin
        Log('TriggerForge did not exit within 30 s.');
        Result := False;
        exit;
      end;
      Sleep(500);
      Waited := Waited + 500;
    end
    else if MsgBox(CustomMessage('AppRunning'), mbError, MB_OKCANCEL) <> IDOK then begin
      Result := False;
      exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := WaitForApp(WizardSilent);
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForApp(UninstallSilent);
end;

function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  if not WizardSilent then
    exit;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;

function WebViewInstalled(Root: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, RuntimeKey, 'pv', Version) and
    (Trim(Version) <> '') and (Trim(Version) <> '0.0.0.0');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not (WebViewInstalled(HKLM32) or WebViewInstalled(HKCU)) then begin
    Log(CustomMessage('MissingWebView'));
    if not WizardSilent then
      MsgBox(CustomMessage('MissingWebView'), mbInformation, MB_OK);
  end;
end;

function RestoreController: Boolean;
var
  ExitCode: Integer;
  Command: String;
begin
  { WaitForApp has already made sure no instance is running. }
  Command := '-NoProfile -NonInteractive -Command "' +
    '$ErrorActionPreference = ''Stop''; ' +
    '$exe = [IO.Path]::GetFullPath(''TriggerForge.exe''); ' +
    '$p = Start-Process -FilePath $exe -ArgumentList ''--restore'', ''--quiet'' -PassThru; ' +
    'if (!$p.WaitForExit(20000)) { $p.Kill(); exit 124 }; exit $p.ExitCode"';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Command, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Log(Format('Controller recovery result: %d', [ExitCode]));
  Result := Result and (ExitCode = 0);
end;

procedure FinishDriverRemoval(const Name, Exe, Args: String);
var
  ExitCode: Integer;
begin
  if Exec(Exe, Args, ExtractFileDir(Exe), SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    if (ExitCode = 1641) or (ExitCode = 3010) then begin
      DriverRestartNeeded := True;
      exit;
    end;
    if ExitCode = 0 then
      exit;
  end;
  Log(Format('%s uninstall failed with result %d.', [Name, ExitCode]));
  MsgBox(FmtMessage(CustomMessage('DriverRemovalFailed'), [Name]), mbError, MB_OK);
end;

procedure RemoveUsbIp;
var
  Command, Exe: String;
  QuoteEnd: Integer;
begin
  if not RegKeyExists(HKLM64, UninstallKey + '\' + UsbIpKey) then
    exit;
  if RegQueryStringValue(HKLM64, UninstallKey + '\' + UsbIpKey, 'UninstallString', Command) then begin
    Command := Trim(Command);
    if Copy(Command, 1, 1) = '"' then begin
      Delete(Command, 1, 1);
      QuoteEnd := Pos('"', Command);
      Exe := Copy(Command, 1, QuoteEnd - 1);
    end
    else
      Exe := Command;
  end;
  if (Exe <> '') and FileExists(Exe) then
    FinishDriverRemoval('usbip-win2', Exe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART')
  else
    MsgBox(FmtMessage(CustomMessage('DriverRemovalFailed'), ['usbip-win2']), mbError, MB_OK);
end;

procedure RemoveHidHide;
var
  Keys: TArrayOfString;
  I: Integer;
  Key, Name, Publisher: String;
  WindowsInstaller: Cardinal;
begin
  if not RegGetSubkeyNames(HKLM64, UninstallKey, Keys) then
    exit;
  for I := 0 to GetArrayLength(Keys) - 1 do begin
    Key := UninstallKey + '\' + Keys[I];
    if RegQueryStringValue(HKLM64, Key, 'DisplayName', Name) and
       (CompareText(Name, 'HidHide') = 0) and
       RegQueryStringValue(HKLM64, Key, 'Publisher', Publisher) and
       (Pos('Nefarius Software Solutions', Publisher) = 1) and
       RegQueryDWordValue(HKLM64, Key, 'WindowsInstaller', WindowsInstaller) and
       (WindowsInstaller = 1) then
      FinishDriverRemoval('HidHide', ExpandConstant('{sys}\msiexec.exe'),
        '/x "' + Keys[I] + '" /qn /norestart');
  end;
end;

procedure RemoveUserData(const Directory: String);
begin
  if DirExists(Directory) and not DelTree(Directory, True, True, True) then
    Log('Could not remove all data from ' + Directory);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step <> usUninstall then
    exit;
  if not RestoreController then begin
    if UninstallSilent or
       (MsgBox(CustomMessage('RestoreFailedAsk'), mbError, MB_YESNO or MB_DEFBUTTON2) <> IDYES) then
      RaiseException(CustomMessage('RestoreFailed'));
    Log('Uninstall continues after a failed controller recovery.');
  end;

  RegDeleteValue(HKCU, RunKey, 'TriggerForge');
  RegDeleteValue(HKCU, RunKey, 'Tactile');
  DeleteFile(ExpandConstant('{userprograms}\TriggerForge — Restore controller.lnk'));
  DeleteFile(ExpandConstant('{userprograms}\TriggerForge — Kontrolcüyü geri getir.lnk'));
  DeleteFile(ExpandConstant('{userprograms}\Tactile — Kontrolcüyü geri getir.lnk'));
  DeleteFile(ExpandConstant('{commonprograms}\TriggerForge.lnk'));
  DeleteFile(ExpandConstant('{commonprograms}\TriggerForge — Restore controller.lnk'));
  DeleteFile(ExpandConstant('{commonprograms}\TriggerForge — Kontrolcüyü geri getir.lnk'));

  if UninstallSilent then
    exit;
  if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
    RemoveUserData(ExpandConstant('{userappdata}\TriggerForge'));
    RemoveUserData(ExpandConstant('{localappdata}\TriggerForge'));
    { Folders of the earlier Tactile builds hold the same profiles. }
    RemoveUserData(ExpandConstant('{userappdata}\Tactile'));
    RemoveUserData(ExpandConstant('{localappdata}\Tactile'));
  end;
  if MsgBox(CustomMessage('RemoveDrivers'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
    RemoveUsbIp;
    RemoveHidHide;
  end;
end;

function UninstallNeedRestart: Boolean;
begin
  Result := DriverRestartNeeded;
end;
