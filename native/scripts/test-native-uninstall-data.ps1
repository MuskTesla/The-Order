param([Parameter(Mandatory)][string]$InnoCompiler, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = [IO.Path]::GetFullPath((Join-Path $repo '.artifacts')).TrimEnd('\')
$root = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { Join-Path $base ('usability-20261011/uninstall-check-' + [guid]::NewGuid().ToString('N')) }
if (!$root.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $root)) { throw 'Fresh workspace test root required.' }
$null = New-Item -ItemType Directory -Path $root, "$root/ordinary/cache", "$root/linked", "$root/external", "$root/keep"
[IO.File]::WriteAllText("$root/ordinary/cache/image.txt", 'synthetic cache')
[IO.File]::WriteAllText("$root/external/game.txt", 'synthetic game sentinel')
[IO.File]::WriteAllText("$root/keep/library.txt", 'synthetic preserved data')
$null = New-Item -ItemType Junction -Path "$root/linked/redirect" -Target "$root/external"
$source = Get-Content -LiteralPath (Join-Path $repo 'native/installer/TheOrder.iss') -Raw
$helper = [regex]::Match($source, '(?s)function DataTreeSafe\(.*?(?=function InitializeUninstall)').Value
if (!$helper) { throw 'Missing actual installer cleanup helper.' }
$harness = @"
[Setup]
AppName=The Order isolated cleanup check
AppVersion=1
DefaultDirName=$root\unused
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir=$root
OutputBaseFilename=check
[Code]
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
$helper
function InitializeSetup: Boolean;
begin
  Result := False;
  if not DataTreeSafe('$root\ordinary') then exit;
  if DataTreeSafe('$root\linked') then exit;
  if DataTreeSafe('$root\linked\redirect\child') then exit;
  if not DelTree('$root\ordinary', True, True, True) then exit;
  if DirExists('$root\ordinary') then exit;
  if not FileExists('$root\external\game.txt') then exit;
  if not FileExists('$root\keep\library.txt') then exit;
  SaveStringToFile('$root\PASS.txt', 'PASS: ordinary cleanup; child and ancestor links rejected; external and retained data preserved.', False);
end;
"@
[IO.File]::WriteAllText("$root/check.iss", $harness, [Text.UTF8Encoding]::new($true))
& $InnoCompiler /Qp "$root/check.iss" *> "$root/build.log"
if ($LASTEXITCODE -ne 0) { throw "Cleanup harness compilation failed: $root/build.log" }
$process = Start-Process -FilePath "$root/check.exe" -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
if (!(Test-Path -LiteralPath "$root/PASS.txt")) { throw "Cleanup harness failed: $root" }
Get-Content -LiteralPath "$root/PASS.txt"
# Leave the small isolated synthetic evidence in place; no real user profile was inspected.
Write-Output $root
