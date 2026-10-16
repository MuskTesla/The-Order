; SPDX-License-Identifier: AGPL-3.0-or-later
; Build only through scripts/package-native-installer.ps1, which checks every input.
#ifndef PayloadDirectory
  #error PayloadDirectory is required
#endif
#ifndef AddonDirectory
  #error AddonDirectory is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef PayloadEntries
  #error Verified PayloadEntries is required
#endif
#ifndef PayloadChecks
  #error Verified PayloadChecks is required
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "1.1.0"
#endif
#ifndef ReleaseFileVersion
  #define ReleaseFileVersion "1.1.0.0"
#endif
#ifdef PublicBeta
  #define ProductName "The Order Beta"
  #define SetupId "{{51CA28EA-2533-4959-90D7-466839AE640F}"
  #define SetupIdentity "TheOrder.Beta.Setup.51CA28EA-2533-4959-90D7-466839AE640F"
  #define AppUserId "dev.local.kungalmanager.beta"
  #define ProfileRoot "{localappdata}\TheOrder-Beta\Profiles\Beta"
#else
  #define ProductName "The Order"
  #define SetupId "{{F7940B13-6814-42F7-AB71-69A9901D7DB3}"
  #define SetupIdentity "TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3"
  #define AppUserId "dev.local.kungalmanager"
  #define ProfileRoot "{localappdata}\TheOrder\Profiles\Production"
#endif

[Setup]
AppId={#SetupId}
AppName={#ProductName}
AppVersion={#ReleaseVersion}
AppVerName={#ProductName} {#ReleaseVersion}
AppPublisher=The Order Project
AppPublisherURL=https://github.com/Ph-Rank/The-Order
AppSupportURL=https://github.com/Ph-Rank/The-Order/issues
AppUpdatesURL=https://github.com/Ph-Rank/The-Order/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableDirPage=no
DirExistsWarning=no
AllowUNCPath=no
AllowNetworkDrive=no
OutputDir={#OutputDirectory}
OutputBaseFilename=The-Order-{#ReleaseVersion}-Setup-x64
SetupIconFile={#PayloadDirectory}\Assets\Brand.Light.ico
UninstallDisplayIcon={app}\TheOrder.exe
LicenseFile={#PayloadDirectory}\LICENSE-TheOrder.txt
WizardStyle=modern dynamic
Compression=lzma2/max
; Separate compression streams keep unchanged files reusable for differential downloads.
SolidCompression=no
DiskSpanning=no
CloseApplications=no
RestartApplications=no
SetupMutex={#SetupIdentity}
UninstallDisplayName={#ProductName}
UninstallLogMode=append
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoVersion={#ReleaseFileVersion}
VersionInfoDescription={#ProductName} 安装程序
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#ReleaseFileVersion}
VersionInfoProductTextVersion={#ReleaseVersion}

[Languages]
Name: "zhHans"; MessagesFile: "languages\ChineseSimplified.isl"

[Messages]
#ifdef PublicBeta
WelcomeLabel2=将安装独立的 The Order Beta，公开测试版功能仍可能调整。%n%n程序和资料与稳定版分离，从空书架开始，不自动导入稳定版资料。卸载 Beta 保留其资料；不会更改稳定版。
FinishedLabel=The Order Beta 已安装完成。%n%n请从独立的 Beta 入口打开。问题反馈：github.com/Ph-Rank/The-Order/issues。关闭窗口后可在托盘选择“退出”。
ConfirmUninstall=确定要卸载 The Order Beta 吗？%n%nBeta 资料会保留。稳定版程序、资料和游戏文件不受影响。
#else
WelcomeLabel2=将安装 The Order，所需运行文件会自动放入安装目录。%n%n安装后从桌面或开始菜单打开即可。升级保留书架、设置和游玩记录；卸载时可选择保留或删除使用数据。
FinishedLabel=The Order 已安装完成。%n%n以后可从桌面或开始菜单打开。需要完全退出时，请在系统托盘选择“退出”。
ConfirmUninstall=确定要卸载 The Order 吗？%n%n接下来可选择保留或删除使用数据，游戏文件不会删除。
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
; Every expected payload/entry/license hash is compiled locally. Only changed/missing files are copied.
#include PayloadEntries
; Keep exactly one installer as the next differential base. Normal uninstall owns/removes it.
; This is a local copy of this Setup, never another download or a runnable patch instruction.
Source: "{srcexe}"; DestDir: "{app}"; DestName: "update-base.bin"; Flags: external ignoreversion

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\TheOrder.exe"; WorkingDir: "{app}"; IconFilename: "{code:ShortcutIcon}"; AppUserModelID: "{#AppUserId}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\TheOrder.exe"; WorkingDir: "{app}"; IconFilename: "{code:ShortcutIcon}"; AppUserModelID: "{#AppUserId}"; Tasks: desktopicon
Name: "{group}\卸载 {#ProductName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\TheOrder.exe"; Description: "打开 The Order"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
#include "PayloadFiles.iss"
#include PayloadChecks

function ShortcutIcon(Param: String): String;
var Light: Cardinal;
begin
  Light := 0;
  RegQueryDWordValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize', 'SystemUsesLightTheme', Light);
  if Light <> 0 then Result := ExpandConstant('{app}\Assets\Brand.Light.ico')
  else Result := ExpandConstant('{app}\Assets\Brand.Dark.ico');
end;

function RunningError: String;
var
  Locator, Service, Processes, Process: Variant;
  ProcessIndex: Integer;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''TheOrder.WinUI.exe''');
#ifdef PublicBeta
    for ProcessIndex := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(ProcessIndex);
      if VarIsNull(Process.ExecutablePath) then begin
        Result := '无法确认运行中的程序路径，未开始安装。'; Exit;
      end;
      if CompareText(ExtractFileDir(Process.ExecutablePath), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0 then begin
        Result := '这个 Beta 仍在运行。请先从其托盘正常退出，再安装或卸载。'; Exit;
      end;
    end;
#else
    if Processes.Count > 0 then
      Result := 'The Order 仍在运行。请在系统托盘选择“退出”，保存编辑后重新运行安装或卸载。';
#endif
  except
    Result := '无法确认 The Order 是否已退出，本次操作未开始。请稍后重试。';
  end;
end;

function PathsOverlap(Left, Right: String): Boolean;
begin
  Left := Lowercase(AddBackslash(RemoveBackslashUnlessRoot(ExpandFileName(Left))));
  Right := Lowercase(AddBackslash(RemoveBackslashUnlessRoot(ExpandFileName(Right))));
  Result := (Pos(Left, Right) = 1) or (Pos(Right, Left) = 1);
end;

function TargetError: String;
var
  Target, Parent: String;
  Attributes: Cardinal;
  FindRec: TFindRec;
  HasContents: Boolean;
begin
  Result := '';
  Target := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  if (Length(Target) <= 3) or (Target[2] <> ':') or
     PathsOverlap(Target, ExpandConstant('{localappdata}\TheOrder')) or
     PathsOverlap(Target, ExpandConstant('{localappdata}\TheOrder-RC')) or
     PathsOverlap(Target, ExpandConstant('{localappdata}\TheOrder-Beta')) or
     PathsOverlap(Target, ExpandConstant('{userappdata}\KunGalManager')) then begin
    Result := '请使用独立的程序目录，不能安装到资料目录或磁盘根目录。';
    Exit;
  end;
#ifdef PublicBeta
  if PathsOverlap(Target, ExpandConstant('{localappdata}\Programs\The Order')) then begin
    Result := 'Beta 必须安装到独立目录，不能覆盖稳定版。'; Exit;
  end;
#endif
  Parent := Target;
  while Parent <> '' do begin
    Attributes := GetFileAttributesW(Parent);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin
      Result := '安装目录包含链接或目录联接。请改用普通的本地文件夹。';
      Exit;
    end;
    if Parent = ExtractFileDir(Parent) then Break;
    Parent := ExtractFileDir(Parent);
  end;
  if not PayloadPathsSafe then begin
    Result := '安装目标包含链接文件或目录，本次安装未开始。请检查安装目录。';
    Exit;
  end;
  HasContents := False;
  Attributes := GetFileAttributesW(AddBackslash(Target) + 'update-base.bin');
  if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin
    Result := '更新缓存是链接文件。请先检查安装目录，本次安装未开始。';
    Exit;
  end;
  if FindFirst(AddBackslash(Target) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then HasContents := True;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if HasContents and (GetIniString('Installer', 'Identity', '',
      AddBackslash(Target) + 'installer-identity.ini') <> '{#SetupIdentity}') then
    Result := '这个目录已有其他文件。请选择新的程序目录，已有便携版无需删除，书架资料会继续保留。';
end;

function InitializeSetup: Boolean;
var
  Error: String;
begin
  Error := RunningError;
  Result := Error = '';
  if not Result then begin
    Log(Error);
    if not WizardSilent then MsgBox(Error, mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RunningError;
  if Result = '' then Result := TargetError;
end;

var DeleteUserData: Boolean;

// Never follow a junction/symlink, including one in a parent or an image-cache subtree.
function DataTreeSafe(const Root: String): Boolean;
var Item: TFindRec; Path: String; Attributes: LongWord;
begin
  Result := False;
  Path := Root;
  while Path <> '' do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then exit;
    if ExtractFileDir(Path) = Path then break;
    Path := ExtractFileDir(Path);
  end;
  Result := True;
  if FindFirst(AddBackslash(Root) + '*', Item) then begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin
          if (Item.Attributes and $400) <> 0 then begin Result := False; break; end;
          if (Item.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            if not DataTreeSafe(AddBackslash(Root) + Item.Name) then begin Result := False; break; end;
        end;
      until not FindNext(Item);
    finally FindClose(Item); end;
  end;
end;

function InitializeUninstall: Boolean;
var
  Error: String;
  Choice: Integer;
begin
  DeleteUserData := False;
  Error := RunningError;
  Result := Error = '';
  if not Result then begin
    Log(Error);
    if not UninstallSilent then MsgBox(Error, mbError, MB_OK);
  end;
  #ifndef PublicBeta
  if Result and not UninstallSilent then begin
    Choice := MsgBox('是否同时删除本机默认资料目录中的全部使用数据？' + #13#10 + #13#10 +
      '是：删除书架、个人编辑、安装记录、游玩记录、图片缓存、设置、登录令牌、API 密钥及同步记录。' + #13#10 +
      '否（默认）：保留全部使用数据，方便以后重装。' + #13#10 +
      '取消：退出卸载。' + #13#10 + #13#10 +
      '游戏文件、手动导出的备份和自定义 Test/Development 资料目录不受影响。' + #13#10 +
      ExpandConstant('{#ProfileRoot}'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
    DeleteUserData := Choice = IDYES;
    Result := Choice <> IDCANCEL;
  end;
  #endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Root: String;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then begin
    Root := ExpandConstant('{#ProfileRoot}');
    if not DataTreeSafe(Root) then
      MsgBox('程序已卸载，但资料目录含链接，已保留数据。请自行检查后清理：' + #13#10 + Root, mbError, MB_OK)
    else if DirExists(Root) and not DelTree(Root, True, True, True) then
      MsgBox('程序已卸载，但部分资料被占用或无权限删除，请自行清理：' + #13#10 + Root, mbError, MB_OK);
  end;
end;
