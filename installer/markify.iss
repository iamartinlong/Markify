; Markify Inno Setup 脚本
;
; 设计要点：
; 1. 用通配符安装 staging 目录下的全部文件，而不是硬编码文件名。
;    AOT 产物未来若多一个原生库，这里不需要改；也避免漏装导致启动崩溃
;    （缺 libSkiaSharp.dll 时 SkiaSharp 会在启动阶段抛 DllNotFoundException）。
; 2. 默认按用户安装（PrivilegesRequired=lowest），不弹 UAC。轻量工具没必要
;    要求管理员权限；需要装到 Program Files 时用户可在安装界面切换。
; 3. 文件关联写在 HKA（管理员安装时 HKLM，否则 HKCU），卸载时自动清理。
; 4. 注册 OpenWithProgids，让 Markify 出现在「打开方式」列表里，但不强行
;    篡改用户的 .md 默认程序——设为默认是可选任务，且 Windows 10+ 还需要
;    用户自己确认一次（UserChoice 哈希保护，程序无法绕过）。

#define MyAppName "Markify"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
; staging 目录由调用方通过 /DStageDir=... 传入（默认相对 installer/ 的 stage）。
#ifndef StageDir
  #define StageDir "stage"
#endif
#define MyAppPublisher "iamartinlong"
#define MyAppUrl "https://github.com/iamartinlong/Markify"
#define MyAppExeName "Markify.exe"
#define MyProgId "Markify.Markdown"

[Setup]
; AppId 必须稳定不变，否则升级会变成并存安装。
AppId={{8B5E1C42-9A3D-4F6B-B0C7-2D5A8E14F9C1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile={#SourcePath}\..\LICENSE.txt

; 按用户安装，无需管理员权限；安装界面允许切换为全局安装。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#SourcePath}\..\artifacts\installer
OutputBaseFilename=Markify-{#MyAppVersion}-setup
SetupIconFile={#SourcePath}\..\src\Markify\Assets\avalonia-logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; 通知 shell 关联已变化，免得到资源管理器里看不到新图标/关联。
ChangesAssociations=yes

; 应用正在运行时提示关闭，避免文件占用导致升级失败。
CloseApplications=yes
RestartApplications=no

[Languages]
; Inno Setup 6 不附带简体中文，这里用仓库内的官方 Unofficial 翻译
; （来自 jrsoftware/issrc 对应版本标签），避免依赖本机安装内容。
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "将 Markify 设为 Markdown 文件的默认打开方式"; GroupDescription: "文件关联:"; Flags: checkedonce

[Files]
; PDB 已在 staging 阶段排除；这里再兜底排除一次。
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; --- ProgID：描述「Markdown 文档」这一类型如何被打开 ---
Root: HKA; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; --- 让 Markify 出现在 .md / .markdown 的「打开方式」列表 ---
; 只登记为候选项，不抢占用户当前的默认程序。
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue

; --- 可选：设为默认打开方式 ---
; 注意：这里不能用 uninsdeletevalue——卸载时它会把 .md 的默认值删成空字符串，
; 用户的原有关联就丢了（实测：原本指向 VSCode.md 的机器卸载后变成空值）。
; 备份与还原统一由 [Code] 段处理。
Root: HKA; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.markdown"; ValueType: string; ValueName: ""; ValueData: "{#MyProgId}"; Tasks: associate

; --- 应用路径登记（供「打开方式 → 更多应用」使用）---
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  BackupRoot = 'Software\Markify\Backup';
  Extensions = '.md,.markdown';

{ 读取某个扩展名当前关联的 ProgID；不存在时返回空字符串。 }
function ReadAssoc(const Ext: String): String;
begin
  if not RegQueryStringValue(HKA, 'Software\Classes\' + Ext, '', Result) then
    Result := '';
end;

{ 在安装前备份原有 .md / .markdown 关联，供卸载时还原。
  只在用户勾选了 associate 任务时才需要备份。 }
procedure BackupAssociations();
var
  Ext: String;
  Existing: String;
  Remaining: String;
  P: Integer;
begin
  Remaining := Extensions;
  while Remaining <> '' do
  begin
    P := Pos(',', Remaining);
    if P = 0 then
    begin
      Ext := Remaining;
      Remaining := '';
    end
    else
    begin
      Ext := Copy(Remaining, 1, P - 1);
      Remaining := Copy(Remaining, P + 1, Length(Remaining) - P);
    end;

    Existing := ReadAssoc(Ext);
    { 只备份非 Markify 的关联；重复升级时不会把 Markify 自己当成"原值"。 }
    if (Existing <> '') and (CompareText(Existing, '{#MyProgId}') <> 0) then
    begin
      RegWriteStringValue(HKA, BackupRoot, Ext, Existing);
      Log('backed up ' + Ext + ' -> ' + Existing);
    end;
  end;
end;

{ 卸载时把关联还原成备份值；没有备份则删掉这一项，
  而不是留下一个空字符串（空值会让 Windows 显示"未知应用"）。 }
procedure RestoreAssociations();
var
  Ext: String;
  Remaining: String;
  Backup: String;
  P: Integer;
begin
  Remaining := Extensions;
  while Remaining <> '' do
  begin
    P := Pos(',', Remaining);
    if P = 0 then
    begin
      Ext := Remaining;
      Remaining := '';
    end
    else
    begin
      Ext := Copy(Remaining, 1, P - 1);
      Remaining := Copy(Remaining, P + 1, Length(Remaining) - P);
    end;

    if RegQueryStringValue(HKA, BackupRoot, Ext, Backup) then
    begin
      RegWriteStringValue(HKA, 'Software\Classes\' + Ext, '', Backup);
      Log('restored ' + Ext + ' -> ' + Backup);
    end
    else
    begin
      { 没有备份：只在当前值确实是 Markify 时才删除，避免误删别人的关联。 }
      if CompareText(ReadAssoc(Ext), '{#MyProgId}') = 0 then
        RegDeleteValue(HKA, 'Software\Classes\' + Ext, '');
    end;
  end;

  { 备份键里还留着扩展名子值，是非空键，必须用 including-subkeys 版本清理：
    RegDeleteKeyIfEmpty 只处理空键，普通 RegDeleteKey 删非空键会失败，
    两者都会留下 HKCU\Software\Markify\Backup 残留（实测）。 }
  RegDeleteKeyIncludingSubkeys(HKA, BackupRoot);
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Markify');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    if WizardIsTaskSelected('associate') then
      BackupAssociations();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { 用 usPostUninstall 而不是 usUninstall：后者执行时卸载程序仍会读写备份键，
    清理会失败并留下 HKCU\Software\Markify\Backup 残留（实测）。 }
  if CurUninstallStep = usPostUninstall then
    RestoreAssociations();
end;
