param([Parameter(Mandatory)][string]$PublishedDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [string]$Packages = (Join-Path $env:USERPROFILE '.nuget/packages'))
$ErrorActionPreference='Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.Path]::GetFullPath($PublishedDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith((Join-Path $repo '.artifacts/') , [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Fresh workspace artifact output required; existing candidates are immutable.' }
if (!(Test-Path -LiteralPath (Join-Path $source 'TheOrder.WinUI.pri'))) { throw 'Incomplete publish' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$redistVersion = (Get-Content -LiteralPath (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCRedistVersion.default.txt') -Raw).Trim()
$crt = Join-Path $vs "VC/Redist/MSVC/$redistVersion/x64"
$crt = (Get-ChildItem -LiteralPath $crt -Directory | Where-Object Name -Like 'Microsoft.VC*.CRT' | Select-Object -First 1).FullName
if (!$crt -or !(Test-Path -LiteralPath (Join-Path $crt 'vcruntime140.dll'))) { throw 'Installed licensed x64 VC redistributable directory required' }
[IO.Directory]::CreateDirectory($output) | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $output
Get-ChildItem -LiteralPath $crt -Filter '*.dll' -File | Copy-Item -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'launch-portable-test.ps1') -Destination (Join-Path $output 'launch-test.ps1')
[IO.File]::WriteAllText((Join-Path $output 'Start-Test.cmd'), '@echo off' + "`r`n" + 'setlocal' + "`r`n" + 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-test.ps1"' + "`r`n" + 'set "theOrderTestExit=%errorlevel%"' + "`r`n" + 'if not "%theOrderTestExit%"=="0" pause' + "`r`n" + 'exit /b %theOrderTestExit%' + "`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $output 'README-RC.txt'), @'
The Order - isolated native x64 Release Candidate (Windows 10 1809 / 17763+)
Not approved for Production use or public release. Glass follows the saved setting.
Extract the entire directory. Double-click Start-Test.cmd. No SDK, repository,
seed or helper is required. Data: %LOCALAPPDATA%/TheOrder-RC/Test (native Test).
Logs: native-runtime.log in that profile. Close X hides to tray; tray Exit drains.
launch-test.ps1 -DataDirectory ABSOLUTE_TEST_PATH -Wait uses another Test profile.
-ControlledAdvanced is a legacy launch argument; Auto/Advanced now use ordinary
daily glass without dispersion, subject to system policy. -Diagnostics remains
a separate internal entry; ordinary start has no 250ms JSON or Gallery controls.
Legacy Opaque preferences display as Standard; the manual Off choice was removed.
Program rollback and restoring data snapshots are different operations. Replacing
or removing this program directory does not remove data, covers or game files.
Back up the closed Test profile before upgrade. Never silently overwrite newer
data with a snapshot. Current optional native-material-v1.json is ignored by the
previous native version; the older native-ui schema is unchanged.
This package has NOT yet passed genuine clean-Windows/OS accessibility tests.
Host smoke cannot establish that claim. See release-identity.json for exact scope.
'@, [Text.UTF8Encoding]::new($false))
$licenses = Join-Path $output 'licenses'; [IO.Directory]::CreateDirectory($licenses) | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $repo 'native/TheOrder.WinUI/obj/project.assets.json') -Raw | ConvertFrom-Json
$dependencyLicenses = @()
$packageNames = @($assets.libraries.PSObject.Properties | Where-Object {$_.Value.type -eq 'package'} | Select-Object -ExpandProperty Name)
# Runtime packs are not all present in assets.libraries. Include the pack matching
# the published runtimeconfig, rather than silently omitting runtime notices.
$runtime = Get-Content -LiteralPath (Join-Path $source 'TheOrder.WinUI.runtimeconfig.json') -Raw | ConvertFrom-Json
$netVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (!$netVersion) { throw 'A self-contained .NET runtime identity is required' }
$packageNames += "Microsoft.NETCore.App.Runtime.win-x64/$netVersion"
foreach ($packageName in ($packageNames | Sort-Object -Unique)) {
    $folder=Join-Path $Packages $packageName.ToLowerInvariant()
    $texts = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match 'license|notice|thirdparty')
    foreach ($text in $texts) { Copy-Item -LiteralPath $text.FullName -Destination (Join-Path $licenses ($packageName.Replace('/','-')+'-'+$text.Name)) }
    $nuspec=Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File | Select-Object -First 1
    if ($nuspec) { Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $licenses ($packageName.Replace('/','-')+'.nuspec')) }
    $dependencyLicenses += @{package=$packageName;licenseFiles=@($texts.Name);metadata=$(if($nuspec){$nuspec.Name}else{$null})}
}
$dependencyLicenses | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $licenses 'dependency-license-index.json') -Encoding utf8
@{source=(git -C $repo rev-parse HEAD);sourceDirty=([bool](git -C $repo status --porcelain));createdUtc=[DateTimeOffset]::UtcNow.ToString('o');architecture='x64';minimumWindowsBuild=17763;minimumWindowsVersion='Windows 10 1809';selfContained=$true;windowsAppSdk='2.5.1';vcRuntime=$redistVersion;defaultMode='saved preference';advancedScope='ordinary daily glass without dispersion, subject to system policy; solid window canvas on Windows 10';gate='NOT YET ACCEPTED';cleanWindows='NOT VERIFIED';windows10='NO REAL OS ENVIRONMENT AVAILABLE; implementation and simulated fallbacks only';crtSource=$crt} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-identity.json') -Encoding utf8
@'
Microsoft VC x64 runtime: app-local files from the installed Visual Studio
REDIST directory, version recorded in release-identity.json. Redistribution is
subject to the installed Visual Studio license and redistributable-code terms.
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
https://learn.microsoft.com/en-us/cpp/windows/determining-which-dlls-to-redistribute
App-local runtimes must be serviced with the application; there is no central
installer servicing claim. Windows OS DLLs/UCRT are supplied by Windows 10/11.
NuGet runtime dependency licenses and metadata are in licenses/. Their rights
remain with their owners. Game art and the current brand asset are not relicensed
by the application; see THIRD_PARTY_NOTICES.md. No synthetic covers are shipped.
'@ | Set-Content -LiteralPath (Join-Path $licenses 'VC-RUNTIME-NOTICE.txt') -Encoding utf8
$files=@(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object { @{path=[IO.Path]::GetRelativePath($output,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
@{schema=1;files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'files.json') -Encoding utf8
Write-Output $output
