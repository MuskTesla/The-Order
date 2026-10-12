param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith((Join-Path $repo '.artifacts\'), [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Fresh workspace fixture output required.' }
$compiler = [IO.Path]::GetFullPath($InnoCompiler)
if ((Get-FileHash -LiteralPath $compiler).Hash -ne '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7') { throw 'Verified Inno Setup 6.7.3 required.' }
$null = [IO.Directory]::CreateDirectory($output)
$source = Join-Path $output 'source'
$target = Join-Path $output 'target'
$null = [IO.Directory]::CreateDirectory($source)
$null = [IO.Directory]::CreateDirectory($target)
[IO.File]::WriteAllText((Join-Path $source 'unchanged.txt'), 'same fixture bytes')
[IO.File]::WriteAllText((Join-Path $source 'changed.txt'), 'new fixture bytes')
[IO.File]::WriteAllText((Join-Path $source 'added.txt'), 'new nested fixture bytes')
[IO.File]::WriteAllText((Join-Path $target 'unchanged.txt'), 'same fixture bytes')
[IO.File]::WriteAllText((Join-Path $target 'changed.txt'), 'old fixture bytes')
[IO.File]::WriteAllText((Join-Path $target 'custom.txt'), 'keep unowned fixture bytes')
$oldStamp = [DateTime]::new(2001, 2, 3, 4, 5, 6, [DateTimeKind]::Utc)
[IO.File]::SetLastWriteTimeUtc((Join-Path $target 'unchanged.txt'), $oldStamp)
$helper = Join-Path $repo 'native/installer/PayloadFiles.iss'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('[Setup]')
foreach ($line in @('AppName=The Order selective file fixture','AppVersion=0.0.1','PrivilegesRequired=lowest','Uninstallable=no','CreateUninstallRegKey=no',
 'DisableDirPage=yes','DisableProgramGroupPage=yes','DisableWelcomePage=yes','CloseApplications=no','RestartApplications=no','SolidCompression=no',
 ('DefaultDirName='+$target),('OutputDir='+$output),'OutputBaseFilename=fixture','[Files]')) { $lines.Add($line) }
foreach ($pair in @(@('unchanged.txt','unchanged.txt'),@('changed.txt','changed.txt'),@('added.txt','nested\added.txt'))) {
 $name=$pair[0];$relative=$pair[1];$dest='{app}';$parent=[IO.Path]::GetDirectoryName($relative);if($parent){$dest+='\'+$parent}
 $hash=(Get-FileHash -LiteralPath (Join-Path $source $name)).Hash
 $lines.Add('Source: "'+(Join-Path $source $name)+'"; DestDir: "'+$dest+'"; Flags: ignoreversion; Check: PayloadFileNeedsUpdate('''+$relative+''', '''+$hash+''')')
}
$lines.Add('Source: "{srcexe}"; DestDir: "{app}"; DestName: "update-base.bin"; Flags: external ignoreversion')
$lines.Add('[Code]')
$lines.Add('function GetFileAttributesW(Name: String): Cardinal; external ''GetFileAttributesW@kernel32.dll stdcall'';')
$lines.Add('#include "'+$helper+'"')
$lines.Add('function PrepareToInstall(var NeedsRestart: Boolean): String;')
$lines.Add('begin')
$lines.Add('  Result := '''';')
$lines.Add('  if not PayloadPathIsPlain(''unchanged.txt'') or not PayloadPathIsPlain(''changed.txt'') or not PayloadPathIsPlain(''nested\added.txt'') or not PayloadPathIsPlain(''update-base.bin'') then Result := ''Linked fixture target rejected'';')
$lines.Add('end;')
$script = Join-Path $output 'fixture.iss'
[IO.File]::WriteAllLines($script, $lines, [Text.UTF8Encoding]::new($false))
& $compiler '/Qp' $script 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture compiler failed.' }
$setup = Join-Path $output 'fixture.exe'
$results = [Collections.Generic.List[object]]::new()
function Verify([string]$Name,[bool]$Passed) { $results.Add([ordered]@{name=$Name;passed=$Passed});if(!$Passed){throw ('Fixture failed: '+$Name)} }
function Run([string]$Name) {
 $start=[Diagnostics.ProcessStartInfo]::new($setup);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 foreach($argument in @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG='+(Join-Path $output ($Name+'.log'))))){$start.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($start)
 try { if(!$process.WaitForExit(30000)){throw 'Owned fixture timed out; no daily application touched.'};return $process.ExitCode } finally {$process.Dispose()}
}
Verify 'first-fixture-install-exit' ((Run 'first') -eq 0)
Verify 'unchanged-file-preserves-timestamp' ([IO.File]::GetLastWriteTimeUtc((Join-Path $target 'unchanged.txt')) -eq $oldStamp)
Verify 'changed-file-replaced' ([IO.File]::ReadAllText((Join-Path $target 'changed.txt')) -eq 'new fixture bytes')
Verify 'missing-nested-file-added' ([IO.File]::ReadAllText((Join-Path $target 'nested/added.txt')) -eq 'new nested fixture bytes')
Verify 'unowned-file-preserved' ([IO.File]::ReadAllText((Join-Path $target 'custom.txt')) -eq 'keep unowned fixture bytes')
Verify 'single-cache-copies-current-installer' ((Get-FileHash -LiteralPath (Join-Path $target 'update-base.bin')).Hash -eq (Get-FileHash -LiteralPath $setup).Hash)
$stamps=@{};foreach($name in @('unchanged.txt','changed.txt','nested/added.txt')){$stamps[$name]=[IO.File]::GetLastWriteTimeUtc((Join-Path $target $name))}
Verify 'repeat-fixture-install-exit' ((Run 'repeat') -eq 0)
Verify 'repeat-skips-all-unchanged-payload' (@($stamps.Keys|Where-Object {[IO.File]::GetLastWriteTimeUtc((Join-Path $target $_)) -ne $stamps[$_]}).Count -eq 0)
[IO.File]::WriteAllText((Join-Path $target 'changed.txt'), 'tampered fixture bytes')
Verify 'repair-fixture-install-exit' ((Run 'repair') -eq 0)
Verify 'hash-detects-and-repairs-changed-bytes' ([IO.File]::ReadAllText((Join-Path $target 'changed.txt')) -eq 'new fixture bytes')
# A junction can be created without changing system theme/security policy or requiring symlink privileges.
$linked = Join-Path $output 'linked-target'
$outside = Join-Path $output 'outside-fixture'
$null=[IO.Directory]::CreateDirectory($linked);$null=[IO.Directory]::CreateDirectory($outside)
[IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'outside fixture unchanged')
$null=New-Item -ItemType Junction -Path (Join-Path $linked 'nested') -Target $outside
$start=[Diagnostics.ProcessStartInfo]::new($setup);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR='+$linked),('/LOG='+(Join-Path $output 'linked.log')))){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($start)
try{if(!$process.WaitForExit(30000)){throw 'Owned linked fixture timed out.'};$linkedExit=$process.ExitCode}finally{$process.Dispose()}
Verify 'linked-target-rejected-before-copy' ($linkedExit -ne 0 -and !(Test-Path -LiteralPath (Join-Path $linked 'unchanged.txt')))
Verify 'linked-outside-untouched' (!(Test-Path -LiteralPath (Join-Path $outside 'added.txt')) -and [IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt')) -eq 'outside fixture unchanged')
[ordered]@{passed=@($results|Where-Object passed).Count;failed=@($results|Where-Object {!$_.passed}).Count;results=$results;
 helperSha256=(Get-FileHash -LiteralPath $helper).Hash;fixtureInstallerExecuted=$true;realApplicationInstallerExecuted=$false;
 systemRegistrationCreated=$false;productionOpened=$false;dailyInstallationReplaced=$false;gameFilesAccessed=$false}|
 ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
$results|Format-Table
