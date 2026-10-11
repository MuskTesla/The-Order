param(
    [Parameter(Mandatory)][string]$CandidatePayload,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo '.artifacts')).TrimEnd('\') + '\'
if (!$output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw 'Use a fresh workspace artifact directory; completed installers are immutable.'
}
$originalHash = $null
$CandidatePayload = [IO.Path]::GetFullPath($CandidatePayload)
if (!$CandidatePayload.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate must be a packaged workspace artifact.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $CandidatePayload 'TheOrder.WinUI.exe')).FileVersion -ne '1.0.1.0') { throw 'Build and package application version 1.0.1 before creating this installer.' }
$compiler = [IO.Path]::GetFullPath($InnoCompiler)
$compilerLibrary = Join-Path (Split-Path $compiler) 'ISCmplr.dll'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf) -or !(Test-Path -LiteralPath $compilerLibrary -PathType Leaf) -or
    (Get-FileHash -LiteralPath $compiler).Hash -ne '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7' -or
    (Get-FileHash -LiteralPath $compilerLibrary).Hash -ne '85A1E3090D3A5B85319F001B7C8F9ECFAD45F37EFF030A67BBE29EF58B7AA2C3') {
    throw 'The verified official Inno Setup 6.7.3 compiler is required.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'The installed Visual Studio C++ build tools are required for the GUI entry.' }
$vcVersion = (Get-Content -LiteralPath (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc = Join-Path $vs ('VC/Tools/MSVC/' + $vcVersion)
$cl = Join-Path $vc 'bin/Hostx64/x64/cl.exe'
$sdk = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdkVersion = '10.0.26100.0'
$rc = Join-Path $sdk "bin/$sdkVersion/x64/rc.exe"
if (!(Test-Path -LiteralPath $cl) -or !(Test-Path -LiteralPath $rc)) { throw 'Existing x64 compiler / Windows SDK 10.0.26100 is missing.' }
$payload = Join-Path $output 'payload'
$addon = Join-Path $output 'addon'
$build = Join-Path $output 'launcher-build'
$null = New-Item -ItemType Directory -Path $output, $addon, $build
Copy-Item -LiteralPath $CandidatePayload -Destination $payload -Recurse
$manifest = Get-Content -LiteralPath (Join-Path $payload 'files.json') -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -LiteralPath (Join-Path $payload 'files.json')).Hash
$seen = @{}
if ($manifest.schema -ne 1 -or @($manifest.files).Count -lt 1 -or @(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -ne @($manifest.files).Count + 1) { throw 'Incomplete payload manifest.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    if (!$path.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($path) -or
        !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw 'Published payload changed or escaped its directory.' }
    $seen[$path] = $true
}
$installerSource = Join-Path $repo 'native/installer'
Copy-Item -LiteralPath (Join-Path $payload 'Assets/Brand.Light.ico') -Destination (Join-Path $build 'brand.ico')
Copy-Item -LiteralPath (Join-Path $installerSource 'Launcher.rc') -Destination $build
$oldPath = $env:PATH
Push-Location $build
try {
    $env:PATH = (Split-Path $cl) + ';' + (Split-Path $rc) + ';' + $oldPath
    $resourceArgs = @('/nologo', '/c65001', ('/fo' + (Join-Path $build 'Launcher.res')),
        ('/I' + (Join-Path $sdk "Include/$sdkVersion/um")),
        ('/I' + (Join-Path $sdk "Include/$sdkVersion/shared")), 'Launcher.rc')
    & $rc @resourceArgs 2>&1 | Tee-Object -FilePath (Join-Path $output 'resource-build.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Entry resource build failed.' }
    $compileArgs = @('/nologo', '/std:c++17', '/utf-8', '/O2', '/MT', '/EHsc', '/W4', '/WX', '/guard:cf',
        ('/I' + (Join-Path $vc 'include')),
        ('/I' + (Join-Path $sdk "Include/$sdkVersion/ucrt")),
        ('/I' + (Join-Path $sdk "Include/$sdkVersion/shared")),
        ('/I' + (Join-Path $sdk "Include/$sdkVersion/um")),
        ('/Fo' + (Join-Path $build 'Launcher.obj')), ('/Fe' + (Join-Path $addon 'TheOrder.exe')),
        (Join-Path $installerSource 'Launcher.cpp'), (Join-Path $build 'Launcher.res'), '/link',
        ('/LIBPATH:' + (Join-Path $vc 'lib/x64')),
        ('/LIBPATH:' + (Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")),
        ('/LIBPATH:' + (Join-Path $sdk "Lib/$sdkVersion/um/x64")),
        '/SUBSYSTEM:WINDOWS', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA', '/GUARD:CF',
        '/MANIFEST:EMBED', "/MANIFESTUAC:level='asInvoker' uiAccess='false'", 'kernel32.lib', 'user32.lib', 'shell32.lib')
    & $cl @compileArgs 2>&1 | Tee-Object -FilePath (Join-Path $output 'launcher-build.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GUI entry build failed.' }
} finally {
    Pop-Location
    $env:PATH = $oldPath
}
@"
[Installer]
Identity=TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3
Version=1.0.1
PublishedArchiveSha256=$originalHash
PublishedManifestSha256=$manifestHash
"@ | Set-Content -LiteralPath (Join-Path $addon 'installer-identity.ini') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $installerSource '安装版使用说明.txt') -Destination $addon
$distribution = Join-Path $output 'distribution'
$null = New-Item -ItemType Directory -Path $distribution
$compilerArgs = @('/Qp', ('/DPayloadDirectory=' + $payload), ('/DAddonDirectory=' + $addon),
    ('/DOutputDirectory=' + $distribution), (Join-Path $installerSource 'TheOrder.iss'))
& $compiler @compilerArgs 2>&1 | Tee-Object -FilePath (Join-Path $output 'installer-build.log') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Setup compiler failed.' }
$setup = Join-Path $distribution 'The-Order-1.0.1-Setup-x64.exe'
$sourceRoot = Join-Path $output 'installer-source'
$null = New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'native/scripts')
Copy-Item -LiteralPath $installerSource -Destination (Join-Path $sourceRoot 'native') -Recurse
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $sourceRoot 'native/scripts')
Copy-Item -LiteralPath (Join-Path $repo 'native/scripts/test-native-installer.ps1') -Destination (Join-Path $sourceRoot 'native/scripts')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $sourceRoot
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{path=[IO.Path]::GetRelativePath($sourceRoot, $_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
@{schema=1;version='1.0.1';purpose='Installer and GUI-entry source supplement; application changes require the corresponding complete source';
    correspondingApplicationSource='The-Order-1.0.1-source.zip';originalArchiveSha256=$originalHash;
    innoSetup='6.7.3';windowsSdk=$sdkVersion;visualCpp=$vcVersion;files=$sourceFiles} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sourceRoot 'INSTALLER-SOURCE-MANIFEST.json') -Encoding utf8
Compress-Archive -LiteralPath (Join-Path $sourceRoot 'native'), (Join-Path $sourceRoot 'LICENSE'),
    (Join-Path $sourceRoot 'INSTALLER-SOURCE-MANIFEST.json') -DestinationPath (Join-Path $distribution 'The-Order-1.0.1-installer-source.zip')
$hashLines = @(Get-ChildItem -LiteralPath $distribution -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
})
[IO.File]::WriteAllLines((Join-Path $distribution 'INSTALLER-SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
$receipt = [ordered]@{schema=1;version='1.0.1';createdUtc=[DateTimeOffset]::UtcNow.ToString('o');
    installer=$setup;installerBytes=(Get-Item -LiteralPath $setup).Length;installerSha256=(Get-FileHash -LiteralPath $setup).Hash;
    originalArchiveSha256=$originalHash;originalManifestSha256=$manifestHash;payloadFiles=@($manifest.files).Count + 1;
    originalApplicationUnchanged=(!$CandidatePayload);signing='unsigned';profileOpened=$false;status='BUILT_NOT_YET_VALIDATED'}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-receipt.json') -Encoding utf8
$receipt | ConvertTo-Json -Depth 5
