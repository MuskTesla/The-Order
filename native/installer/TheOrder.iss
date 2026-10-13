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

[Setup]
AppId={{F7940B13-6814-42F7-AB71-69A9901D7DB3}
AppName=The Order
AppVersion=1.1.0
AppVerName=The Order 1.1.0
AppPublisher=The Order Project
AppPublisherURL=https://github.com/Ph-Rank/The-Order
AppSupportURL=https://github.com/Ph-Rank/The-Order/issues
AppUpdatesURL=https://github.com/Ph-Rank/The-Order/releases
DefaultDirName={localappdata}\Programs\The Order
DefaultGroupName=The Order
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
OutputBaseFilename=The-Order-1.1.0-Setup-x64
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
SetupMutex=TheOrder.Setup.1
UninstallDisplayName=The Order
UninstallLogMode=append
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoVersion=1.1.0.0
VersionInfoDescription=The Order 安装程序
VersionInfoProductName=The Order
VersionInfoProductVersion=1.1.0

[Languages]
Name: "zhHans"; MessagesFile: "languages\ChineseSimplified.isl"

[Messages]
WelcomeLabel2=将安装 The Order，所需运行文件会自动放入安装目录。%n%n安装后从桌面或开始菜单打开即可。升级保留书架、设置和游玩记录；卸载时可选择保留或删除使用数据。
FinishedLabel=The Order 已安装完成。%n%n以后可从桌面或开始菜单打开。需要完全退出时，请在系统托盘选择“退出”。
ConfirmUninstall=确定要卸载 The Order 吗？%n%n接下来可选择保留或删除使用数据，游戏文件不会删除。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
; Every expected payload/entry/license hash is compiled locally. Only changed/missing files are copied.
#include PayloadEntries
; Keep exactly one installer as the next differential base. Normal uninstall owns/removes it.
; This is a local copy of this Setup, never another download or a runnable patch instruction.
Source: "{srcexe}"; DestDir: "{app}"; DestName: "update-base.bin"; Flags: external ignoreversion

[Icons]
Name: "{group}\The Order"; Filename: "{app}\TheOrder.exe"; WorkingDir: "{app}"; IconFilename: "{code:ShortcutIcon}"; AppUserModelID: "dev.local.kungalmanager"
Name: "{autodesktop}\The Order"; Filename: "{app}\TheOrder.exe"; WorkingDir: "{app}"; IconFilename: "{code:ShortcutIcon}"; AppUserModelID: "dev.local.kungalmanager"; Tasks: desktopicon
Name: "{group}\卸载 The Order"; Filename: "{uninstallexe}"

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
  Locator, Service, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''TheOrder.WinUI.exe''');
    if Processes.Count > 0 then
      Result := 'The Order 仍在运行。请在系统托盘选择“退出”，保存编辑后重新运行安装或卸载。';
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
     PathsOverlap(Target, ExpandConstant('{userappdata}\KunGalManager')) then begin
    Result := '请使用独立的程序目录，不能安装到资料目录或磁盘根目录。';
    Exit;
  end;
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
      AddBackslash(Target) + 'installer-identity.ini') <> 'TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3') then
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
  if Result and not UninstallSilent then begin
    Choice := MsgBox('是否同时删除本机默认资料目录中的全部使用数据？' + #13#10 + #13#10 +
      '是：删除书架、个人编辑、安装记录、游玩记录、图片缓存、设置、登录令牌、API 密钥及同步记录。' + #13#10 +
      '否（默认）：保留全部使用数据，方便以后重装。' + #13#10 +
      '取消：退出卸载。' + #13#10 + #13#10 +
      '游戏文件、手动导出的备份和自定义 Test/Development 资料目录不受影响。' + #13#10 +
      ExpandConstant('{localappdata}\TheOrder\Profiles\Production'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
    DeleteUserData := Choice = IDYES;
    Result := Choice <> IDCANCEL;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Root: String;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then begin
    Root := ExpandConstant('{localappdata}\TheOrder\Profiles\Production');
    if not DataTreeSafe(Root) then
      MsgBox('程序已卸载，但资料目录含链接，已保留数据。请自行检查后清理：' + #13#10 + Root, mbError, MB_OK)
    else if DirExists(Root) and not DelTree(Root, True, True, True) then
      MsgBox('程序已卸载，但部分资料被占用或无权限删除，请自行清理：' + #13#10 + Root, mbError, MB_OK);
  end;
end;
