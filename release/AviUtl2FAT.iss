#define AppName "AviUtl2 FAT"
#define AppVersion "1.0.0"
#define AppPublisher "AviUtl2 FAT Project"
#ifndef SourcePayload
  #define SourcePayload "..\\dist\\AviUtl2FAT-1.0.0-x64\\Plugin\\AviUtl2FAT"
#endif

[Setup]
AppId={{D63C1F27-3B64-4D1E-8C5F-6D908BDBA100}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={code:GetFatPluginDirectory}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=AviUtl2FAT-Setup-1.0.0-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
InfoBeforeFile=..\QUICKSTART.md

[Files]
Source: "{#SourcePayload}\*"; DestDir: "{code:GetFatPluginDirectory}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\AviUtl2 FAT"; Filename: "{code:GetFatPluginDirectory}\FAT\AviUtl2FAT.App.exe"
Name: "{autodesktop}\AviUtl2 FAT"; Filename: "{code:GetFatPluginDirectory}\FAT\AviUtl2FAT.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; Flags: unchecked

[Code]
var
  PluginPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  PluginPage := CreateInputDirPage(wpSelectDir, 'AviUtl2 Plugin フォルダー', 'AviUtl2のPluginフォルダーを指定してください', 'AviUtl2 FATだけをフォルダー内のAviUtl2FATサブフォルダーへ配置します。他のプラグインやプロジェクトには変更を加えません。', False, '');
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

function GetFatPluginDirectory(Param: String): String;
begin
  if GetRequestedPluginDirectory <> '' then
    Result := AddBackslash(GetRequestedPluginDirectory) + 'AviUtl2FAT'
  else if PluginPage <> nil then
    Result := AddBackslash(PluginPage.Values[0]) + 'AviUtl2FAT'
  else if DirExists(ExpandConstant('{commonappdata}\aviutl2\Plugin')) then
    Result := ExpandConstant('{commonappdata}\aviutl2\Plugin\AviUtl2FAT')
  else
    Result := ExpandConstant('{userappdata}\aviutl2\Plugin\AviUtl2FAT');
end;
