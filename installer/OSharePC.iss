#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppName "OShare PC"
#define MyAppExeName "catshare_gui.exe"




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




[Tasks]
Name: "startup"; Description: "Launch OShare PC when Windows starts"; GroupDescription: "Startup options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked




[Files]
Source: "..\deploy-gui\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs




[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon




[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OsharePC"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Flags: uninsdeletevalue; Tasks: startup




[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch OShare PC"; Flags: nowait postinstall skipifsilent




[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if ActiveLanguage = 'chinesesimplified' then
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'zh-CN', False)
    else if ActiveLanguage = 'chinesetraditional' then
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'zh-TW', False)
    else
      SaveStringToFile(ExpandConstant('{app}\installer-language.txt'), 'en', False);
end;
