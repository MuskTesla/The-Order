param([string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'TheOrder-RC/Test'), [switch]$ControlledAdvanced, [switch]$Diagnostics, [switch]$Wait)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
# Windows PowerShell 5.1 compatible. No SDK, repository, helper or seed required.
$package = $PSScriptRoot
$exe = Join-Path $package 'TheOrder.WinUI.exe'
if (![IO.Path]::IsPathRooted($DataDirectory)) { throw 'Use an absolute isolated Test directory.' }
$profile = [IO.Path]::GetFullPath($DataDirectory)
$formalRoots = @((Join-Path $env:APPDATA 'KunGalManager'), (Join-Path $env:LOCALAPPDATA 'TheOrder/Profiles/Production'))
foreach ($formalRoot in $formalRoots) {
    $production = [IO.Path]::GetFullPath($formalRoot)
    if ($profile -eq $production -or $profile.StartsWith($production + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Production is forbidden for this RC entry.' }
}
if (Test-Path -LiteralPath (Join-Path $profile 'native-profile.json')) {
    $manifest = Get-Content -LiteralPath (Join-Path $profile 'native-profile.json') -Raw | ConvertFrom-Json
    if ($manifest.ProfileKind -ne 'Test') { throw 'Existing profile is not Test; it will not be changed.' }
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'files.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $package $file.path))
    if (!$path.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Candidate file missing or changed: $($file.path). Restore the complete package; no profile was opened." }
}
$prior = @{}
Get-ChildItem Env: | Where-Object { $_.Name.StartsWith('THE_ORDER_NATIVE_') -or $_.Name -in @('KGM_DATA_DIR','KGM_TEST_EXECUTABLE','DOTNET_ROOT','DOTNET_ROOT_X64') } | ForEach-Object { $prior[$_.Name] = $_.Value }
$values = @{THE_ORDER_NATIVE_DATA_DIR=$profile;THE_ORDER_NATIVE_PROFILE_KIND='Test';THE_ORDER_NATIVE_FIXTURE_PROFILE='0';THE_ORDER_NATIVE_DIAGNOSTICS='0';THE_ORDER_NATIVE_VISUAL_TEST='0';THE_ORDER_NATIVE_ADVANCED_TEST='0'}
if ($ControlledAdvanced) { $values.THE_ORDER_NATIVE_ADVANCED_TEST = '1' }
if ($Diagnostics) { $values.THE_ORDER_NATIVE_DIAGNOSTICS = '1'; $values.THE_ORDER_NATIVE_VISUAL_TEST = '1'; $values.THE_ORDER_NATIVE_VISUAL_CONTROL = Join-Path $profile 'test-control' }
$process = $null
try {
    foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process') }
    $process = Start-Process -FilePath $exe -WorkingDirectory $package -PassThru
    [pscustomobject]@{pid=$process.Id;start=$process.StartTime.ToUniversalTime().ToString('o');candidate=$exe;profile=$profile;controlledAdvanced=[bool]$ControlledAdvanced;diagnostics=[bool]$Diagnostics} | ConvertTo-Json
} finally {
    foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable($name, $prior[$name], 'Process') }
}
if ($Wait -and $process) { $process.WaitForExit(); $code=$process.ExitCode; $process.Dispose(); exit $code }
if ($process) { $process.Dispose() }
