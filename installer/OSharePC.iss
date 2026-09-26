#ifndef MyAppVersion
#define MyAppVersion "1.0.7"
#endif
#define MyAppName "OShare PC"
#define MyAppExeName "oshare_gui.exe"



[Setup]
AppId={{B4B9C9C8-5C56-4C06-9A1C-123456789001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=OShare PC
DefaultDirName={localappdata}\Programs\OSharePC
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=OSharePC-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
ShowLanguageDialog=yes
UninstallDisplayIcon={app}\{#MyAppExeName}




[Languages]
; Supported installer languages:
; - English
; - 简体中文
; - 繁體中文
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "{#SourcePath}\languages\ChineseTraditional.isl"




[CustomMessages]
english.StartupGroup=Startup options:
english.StartupTask=Launch OShare PC when Windows starts
english.DesktopGroup=Additional shortcuts:
english.DesktopTask=Create a desktop shortcut
english.LaunchProgram=Launch OShare PC
english.UninstallPrompt=Remove OShare PC application data, settings and logs?

chinesesimplified.StartupGroup=启动选项：
chinesesimplified.StartupTask=Windows 启动时运行 OShare PC
chinesesimplified.DesktopGroup=附加快捷方式：
chinesesimplified.DesktopTask=创建桌面快捷方式
chinesesimplified.LaunchProgram=启动 OShare PC
chinesesimplified.UninstallPrompt=是否同时删除 OShare PC 的应用数据、设置和日志？

chinesetraditional.StartupGroup=啟動選項：
chinesetraditional.StartupTask=Windows 啟動時執行 OShare PC
chinesetraditional.DesktopGroup=其他捷徑：
chinesetraditional.DesktopTask=建立桌面捷徑
chinesetraditional.LaunchProgram=啟動 OShare PC
chinesetraditional.UninstallPrompt=是否同時刪除 OShare PC 的應用程式資料、設定和記錄？




[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:DesktopTask}"; GroupDescription: "{cm:DesktopGroup}"; Flags: unchecked




[Files]
Source: "..\deploy-gui\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs




[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon




[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OsharePC"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Flags: uninsdeletevalue; Tasks: startup




[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent




[Code]
var
  UninstallRemoveAllData: Boolean;

procedure StopOShareProcesses(AppDir: String);
var
  ResultCode: Integer;
  PowerShellCmd: String;
  PowerShellExe: String;
  CleanAppDir: String;
begin
  CleanAppDir := AppDir;
  if CleanAppDir = '' then
    Exit;
  StringChange(CleanAppDir, '''', '''''');

  // Every window the GUI can spawn (main window, the corner receive popup,
  // and the desktop drop panel) is the same oshare_gui.exe re-invoked with
  // different arguments, so a stray popup/panel instance left running from
  // an earlier session can hold the exe or its DLLs locked during an
  // in-place upgrade, silently failing that one file's copy while the rest
  // of the install succeeds. Force-kill every matching process by path, then
  // actively poll until none remain (instead of a single fixed sleep) so the
  // file copy that follows never races a process that is still tearing down.
  PowerShellCmd :=
    '$target = ''' + CleanAppDir + '''.TrimEnd(''\''); ' +
    'try { ' +
      '$wr = [System.Net.WebRequest]::Create(''http://127.0.0.1:8960/api/shutdown''); ' +
      '$wr.Method = ''POST''; ' +
      '$wr.Timeout = 1500; ' +
      '$wr.ContentLength = 0; ' +
      '$res = $wr.GetResponse(); ' +
      '$res.Close(); ' +
    '} catch {}; ' +
    'function Get-OShareProcesses { ' +
      'Get-Process -Name ''oshare_gui'', ''OSharePC'' -ErrorAction SilentlyContinue | Where-Object { ' +
        'try { $_.Path -and $_.Path.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase) } catch { $false } ' +
      '} ' +
    '}; ' +
    'Get-OShareProcesses | ForEach-Object { try { $_.CloseMainWindow() } catch {} }; ' +
    'Start-Sleep -Milliseconds 800; ' +
    'Get-OShareProcesses | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {} }; ' +
    'for ($i = 0; $i -lt 20; $i++) { ' +
      'if (-not (Get-OShareProcesses)) { break }; ' +
      'Start-Sleep -Milliseconds 250; ' +
    '}; ' +
    'Start-Sleep -Milliseconds 300;';

  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellExe) then
    PowerShellExe := 'powershell.exe';

  Exec(PowerShellExe,
    '-NoProfile -NonInteractive -WindowStyle Hidden -Command "' + PowerShellCmd + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopOShareProcesses(ExpandConstant('{app}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if ActiveLanguage = 'chinesesimplified' then
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'zh-CN', False)
    else if ActiveLanguage = 'chinesetraditional' then
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'zh-TW', False)
    else
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'en', False);

    if not WizardIsTaskSelected('startup') then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OsharePC');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  UninstallRemoveAllData := False;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  NetshExe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopOShareProcesses(ExpandConstant('{app}'));
    if not UninstallSilent then
      UninstallRemoveAllData := (MsgBox(CustomMessage('UninstallPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OsharePC');
    DeleteFile(ExpandConstant('{app}\installer-language.txt'));

    if UninstallRemoveAllData then
    begin
      DelTree(ExpandConstant('{localappdata}\OSharePC'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\com.oshare\oshare_gui'), True, True, True);
      RemoveDir(ExpandConstant('{userappdata}\com.oshare'));
      DelTree(ExpandConstant('{userappdata}\OShare'), True, True, True);

      NetshExe := ExpandConstant('{sys}\netsh.exe');
      if not FileExists(NetshExe) then
        NetshExe := 'netsh.exe';
      Exec(NetshExe, 'advfirewall firewall delete rule name="OSharePC"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(NetshExe, 'advfirewall firewall delete rule name="OSharePCBandEcho"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;

    RemoveDir(ExpandConstant('{app}'));
  end;
end;
