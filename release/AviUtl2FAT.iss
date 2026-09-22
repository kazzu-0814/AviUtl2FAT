#define AppName "AviUtl2 AltFactor"
#ifndef AppVersion
  #define AppVersion "2.0.2"
#endif
#define AppPublisher "AviUtl2 AltFactor Project"
#ifndef SourcePayload
  #define SourcePayload "..\\dist\\AviUtl2FAT-1.0.0-x64\\Plugin\\AviUtl2FAT"
#endif

[Setup]
AppId={{D63C1F27-3B64-4D1E-8C5F-6D908BDBA100}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={code:GetFatPluginDirectory}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=AviUtl2-AltFactor-Setup-{#AppVersion}-x64
#ifdef SignToolName
SignTool={#SignToolName}
#endif
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#AppName}
UninstallDisplayIcon={code:GetFatPluginDirectory}\FAT\AviUtl2FAT.App.exe
LicenseFile=..\LICENSE
InfoBeforeFile=..\QUICKSTART.md

[Files]
Source: "{#SourcePayload}\*"; DestDir: "{code:GetFatPluginDirectory}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\AviUtl2 AltFactor"; Filename: "{code:GetFatPluginDirectory}\FAT\AviUtl2FAT.App.exe"
Name: "{autoprograms}\AviUtl2 AltFactor をアンインストール"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AviUtl2 AltFactor"; Filename: "{code:GetFatPluginDirectory}\FAT\AviUtl2FAT.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; Flags: unchecked

[Code]
var
  PluginPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  PluginPage := CreateInputDirPage(wpSelectDir, 'AviUtl2 Plugin フォルダー', 'AviUtl2のPluginフォルダーを指定してください', 'AviUtl2 AltFactorだけをフォルダー内の既存AviUtl2FAT互換サブフォルダーへ配置します。他のプラグインやプロジェクトには変更を加えません。', False, '');
  PluginPage.Add('Plugin フォルダー:');
  if DirExists(ExpandConstant('{commonappdata}\aviutl2\Plugin')) then
    PluginPage.Values[0] := ExpandConstant('{commonappdata}\aviutl2\Plugin')
  else
    PluginPage.Values[0] := ExpandConstant('{userappdata}\aviutl2\Plugin');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PluginPage.ID then begin
    if not DirExists(PluginPage.Values[0]) then begin
      MsgBox('存在するAviUtl2 Pluginフォルダーを指定してください。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function GetRequestedPluginDirectory: String;
begin
  Result := ExpandConstant('{param:PluginDir|}');
  if Result = '' then
    Result := ExpandConstant('{param:PLUGIN_DIR|}');
end;

function SelectAltFactorPluginDirectory(BaseDirectory: String): String;
begin
  { Never move an existing FAT installation. A clean install receives the new name. }
  if DirExists(AddBackslash(BaseDirectory) + 'AviUtl2-AltFactor') then
    Result := AddBackslash(BaseDirectory) + 'AviUtl2-AltFactor'
  else if DirExists(AddBackslash(BaseDirectory) + 'AviUtl2FAT') then
    Result := AddBackslash(BaseDirectory) + 'AviUtl2FAT'
  else
    Result := AddBackslash(BaseDirectory) + 'AviUtl2-AltFactor';
end;

function GetFatPluginDirectory(Param: String): String;
begin
  if GetRequestedPluginDirectory <> '' then
    Result := SelectAltFactorPluginDirectory(GetRequestedPluginDirectory)
  else if PluginPage <> nil then
    Result := SelectAltFactorPluginDirectory(PluginPage.Values[0])
  else if DirExists(ExpandConstant('{commonappdata}\aviutl2\Plugin')) then
    Result := SelectAltFactorPluginDirectory(ExpandConstant('{commonappdata}\aviutl2\Plugin'))
  else
    Result := SelectAltFactorPluginDirectory(ExpandConstant('{userappdata}\aviutl2\Plugin'));
end;

function ExpectedApplicationPath: String;
begin
  Result := ExpandFileName(AddBackslash(GetFatPluginDirectory('')) + 'FAT\AviUtl2FAT.App.exe');
end;

function ShortcutTargetsCurrentApplication(const ShortcutPath: String): Boolean;
var
  Shell, Shortcut: Variant;
  TargetPath: String;
begin
  Result := False;
  if not FileExists(ShortcutPath) then
    exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    TargetPath := ExpandFileName(Shortcut.TargetPath);
    Result := CompareText(TargetPath, ExpectedApplicationPath) = 0;
  except
    Log('Could not inspect shortcut: ' + ShortcutPath);
  end;
end;

procedure ReplaceLegacyShortcut(const LegacyPath, AltFactorPath: String);
begin
  { Only the canonical old installer shortcut in a standard location is
    considered.  A same-named link is removed only after an AltFactor link is
    present and points to this installation's executable. }
  if not ShortcutTargetsCurrentApplication(LegacyPath) then
    exit;
  if not ShortcutTargetsCurrentApplication(AltFactorPath) then begin
    Log('Keeping legacy shortcut because the new shortcut was not verified: ' + LegacyPath);
    exit;
  end;
  if DeleteFile(LegacyPath) then
    Log('Replaced legacy FAT shortcut: ' + LegacyPath)
  else
    Log('Could not remove legacy FAT shortcut: ' + LegacyPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    ReplaceLegacyShortcut(ExpandConstant('{autoprograms}\AviUtl2 FAT.lnk'),
      ExpandConstant('{autoprograms}\AviUtl2 AltFactor.lnk'));
    if WizardIsTaskSelected('desktopicon') then
      ReplaceLegacyShortcut(ExpandConstant('{autodesktop}\AviUtl2 FAT.lnk'),
        ExpandConstant('{autodesktop}\AviUtl2 AltFactor.lnk'));
  end;
end;
