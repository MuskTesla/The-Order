param([Parameter(Mandatory)][string]$Plan, [switch]$ValidateOnly, [switch]$ProbeHandoff)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
Import-Module (Join-Path $PSHOME 'Modules\CimCmdlets\CimCmdlets.psd1') -Force -ErrorAction Stop
if ($ProbeHandoff -and !$ValidateOnly) { throw 'Handoff probing requires validation-only; it can never install.' }
# Windows PowerShell 5.1; no downloaded commands, force-close, Unblock-File or protection changes.
function PlainPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.IndexOf(':',2) -ge 0 -or $Path -match '[\x00-\x1f"<>|*?]' -or $Path.StartsWith('\\')) { throw 'InvalidPath' }
    $value = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    for ($part=[IO.DirectoryInfo]::new($value); $part; $part=$part.Parent) {
        if ((Test-Path -LiteralPath $part.FullName) -and (([IO.File]::GetAttributes($part.FullName) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'LinkedPath' }
    }
    return $value
}
function Same([string]$A,[string]$B) { return $A.Equals($B,[StringComparison]::OrdinalIgnoreCase) }
function Overlap([string]$A,[string]$B) { return (Same $A $B) -or $A.StartsWith($B+'\',[StringComparison]::OrdinalIgnoreCase) -or $B.StartsWith($A+'\',[StringComparison]::OrdinalIgnoreCase) }
function WriteJson([string]$Path,$Value) {
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,$null) } else { [IO.File]::Move($temporary,$Path) }
}
function InstallerValid($Job) {
    $path=PlainPath $Job.installer
    if ((Get-Item -LiteralPath $path).Length -ne $Job.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Job.sha256) { throw 'InstallerHash' }
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($version.ProductName.Trim() -ne 'The Order' -or ([version]$version.FileVersion.Trim()).ToString(3) -ne $Job.version) { throw 'InstallerIdentity' }
}
function LoadJob([string]$Path,[bool]$OnlyValidate) {
    $path=PlainPath $Path
    if ([IO.Path]::GetFileName($path) -ne 'plan.json' -or (Get-Item -LiteralPath $path).Length -gt 16384) { throw 'InvalidPlan' }
    $job=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if ($job.schema -ne 1 -or $job.id -notmatch '^[a-f0-9]{32}$' -or $job.version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$' -or $job.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $job.workerSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'InvalidIdentity' }
    $version=[version]$job.version
    if ($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535 -or $version -le [version]$job.currentVersion -or $job.bytes -lt 2 -or $job.bytes -gt 536870912) { throw 'InvalidVersion' }
    $profile=PlainPath $job.profile; $folder=Split-Path -LiteralPath $path
    $expected=Join-Path (Join-Path $profile 'updates') $job.id
    $installer=PlainPath $job.installer; $downloadFolder=Split-Path -LiteralPath $installer
    if (!(Same $folder $expected) -or !(Same (Split-Path -LiteralPath $downloadFolder) (Join-Path $profile 'updates')) -or
        ([IO.Path]::GetFileName($downloadFolder) -notmatch '^[a-f0-9]{32}$') -or
        ([IO.Path]::GetFileName($installer) -cne ('The-Order-'+$job.version+'-Setup-x64.exe')) -or
        !(Same (PlainPath $job.result) (Join-Path $profile 'updates\last-result.json'))) { throw 'PlanBoundary' }
    $manifest=Join-Path $profile 'native-profile.json'; $null=PlainPath $manifest
    if ((Get-Item -LiteralPath $manifest).Length -gt 16384) { throw 'ProfileIdentity' }
    $stored=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
    # PhysicalRoot is the native store's directory identity SHA-256, not a path.
    if ($stored.Version -ne 1 -or $stored.State -ne 'Ready' -or $job.physicalRootHash -notmatch '^[A-Fa-f0-9]{64}$' -or
        $stored.PhysicalRoot -ne $job.physicalRootHash -or $stored.ProfileId -ne $job.profileId -or $stored.StoreId -ne $job.storeId -or
        $job.profileId -notmatch '^[A-Fa-f0-9-]{36}$' -or $job.storeId -notmatch '^[A-Fa-f0-9-]{36}$') { throw 'ProfileIdentity' }
    if (!$OnlyValidate -and ($stored.ProfileKind -ne 'Production' -or !(Same $profile (Join-Path $env:LOCALAPPDATA 'TheOrder\Profiles\Production')))) { throw 'ProductionOnly' }
    if ($OnlyValidate -and $stored.ProfileKind -notin @('Test','Production')) { throw 'ProfileIdentity' }
    $target=PlainPath $job.target
    foreach ($protected in @($profile,(Join-Path $env:LOCALAPPDATA 'TheOrder'),(Join-Path $env:APPDATA 'KunGalManager'))) {
        if (Overlap $target ([IO.Path]::GetFullPath($protected).TrimEnd('\'))) { throw 'TargetBoundary' }
    }
    if ($target.Length -le 3) { throw 'TargetBoundary' }
    if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force|Select-Object -First 1).Count -gt 0) {
        $marker=Join-Path $target 'installer-identity.ini'; $null=PlainPath $marker
        if (!(Test-Path -LiteralPath $marker) -or (Get-Item -LiteralPath $marker).Length -gt 4096 -or !(Test-Path -LiteralPath (Join-Path $target 'unins000.exe')) -or
            @((Get-Content -LiteralPath $marker)|Where-Object {$_.Trim() -eq 'Identity=TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3'}).Count -ne 1) { throw 'UnmanagedTarget' }
    }
    if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -ne $job.workerSha256) { throw 'WorkerIdentity' }
    if ($job.parentPid -lt 1 -or $job.parentStartTicks -notmatch '^[0-9]{16,19}$') { throw 'ParentIdentity' }
    if ($job.commitEvent -cne ('Local\TheOrder.Update.Commit.'+$job.id) -or $job.cancelEvent -cne ('Local\TheOrder.Update.Cancel.'+$job.id)) { throw 'HandoffIdentity' }
    InstallerValid $job
    return $job
}
function RestartApp([string]$Directory) {
    $launcher=Join-Path $Directory 'TheOrder.exe'
    if (Test-Path -LiteralPath $launcher) { $null=PlainPath $launcher; Start-Process -FilePath $launcher -WorkingDirectory $Directory -WindowStyle Hidden | Out-Null }
    else {
        $app=Join-Path $Directory 'TheOrder.WinUI.exe'; $null=PlainPath $app
        Start-Process -FilePath $app -ArgumentList '--allow-production-profile' -WorkingDirectory $Directory -WindowStyle Hidden | Out-Null
    }
}
$job=$null; $parentExited=$false; $commitSignal=$null; $cancelSignal=$null
try {
    $job=LoadJob $Plan ([bool]$ValidateOnly)
    $folder=Split-Path -LiteralPath ([IO.Path]::GetFullPath($Plan))
    if ($ValidateOnly -and !$ProbeHandoff) { [pscustomobject]@{validated=$true;version=$job.version;installationStarted=$false;profileOpened=$false}|ConvertTo-Json; exit 0 }
    $commitSignal=[Threading.EventWaitHandle]::OpenExisting($job.commitEvent)
    $cancelSignal=[Threading.EventWaitHandle]::OpenExisting($job.cancelEvent)
    WriteJson (Join-Path $folder 'ready.json') @{id=$job.id;ready=$true}
    $deadline=[Diagnostics.Stopwatch]::StartNew(); $committed=$false
    while ($deadline.Elapsed.TotalSeconds -lt 120) {
        if ($cancelSignal.WaitOne(0)) {
            if ($ProbeHandoff) { WriteJson (Join-Path $folder 'handoff-probe.json') @{authorized=$false;reason='Canceled';installationStarted=$false} }
            exit 0
        }
        $committed=$commitSignal.WaitOne(0)
        $owner=Get-Process -Id $job.parentPid -ErrorAction SilentlyContinue
        $parentExited=!$owner -or $owner.StartTime.ToUniversalTime().Ticks.ToString() -ne $job.parentStartTicks
        if ($owner) { $owner.Dispose() }
        if ($committed -and $parentExited) { break }
        if ($parentExited -and !$committed) {
            if ($ProbeHandoff) { WriteJson (Join-Path $folder 'handoff-probe.json') @{authorized=$false;reason='NotCommitted';installationStarted=$false} }
            exit 0
        }
        Start-Sleep -Milliseconds 200
    }
    if (!$committed -or !$parentExited) { throw 'ExitNotCommitted' }
    if ($ProbeHandoff) {
        WriteJson (Join-Path $folder 'handoff-probe.json') @{authorized=$true;reason='CommittedAndExactOwnerExited';installationStarted=$false}
        exit 0
    }
    # Revalidate after shutdown; hold a non-writable handle while installing.
    $job=LoadJob $Plan $false
    $running=Get-CimInstance Win32_Process -Filter "Name='TheOrder.WinUI.exe'"
    if ($running) { throw 'AnotherInstanceRunning' }
    $lock=[IO.File]::Open($job.installer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        InstallerValid $job
        $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="'+$job.target+'" /LOG="'+(Join-Path $folder 'install.log')+'"'
        $setup=Start-Process -FilePath $job.installer -ArgumentList $arguments -WorkingDirectory $folder -PassThru -Wait -WindowStyle Hidden
        try { if ($setup.ExitCode -ne 0) { throw 'SetupFailed' } } finally { $setup.Dispose() }
    } finally { $lock.Dispose() }
    $app=Join-Path $job.target 'TheOrder.WinUI.exe'; $null=PlainPath $app
    if (([version]([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion.Trim())).ToString(3) -ne $job.version) { throw 'InstalledVersionMismatch' }
    WriteJson $job.result @{schema=1;version=$job.version;success=$true;code='Installed'}
    try { RestartApp $job.target }
    catch { WriteJson $job.result @{schema=1;version=$job.version;success=$true;code='InstalledRestartUnavailable'} }
    # The installer download is owned cache, not user data. Retain the bounded log/receipt.
    try { [IO.File]::Delete($job.installer) } catch { }
} catch {
    if ($ValidateOnly) {
        [pscustomobject]@{validated=$false;type=$_.Exception.GetType().Name;category=$_.CategoryInfo.Category.ToString();line=$_.InvocationInfo.ScriptLineNumber}|ConvertTo-Json
    }
    if ($job) {
        try { $null=PlainPath $job.result; WriteJson $job.result @{schema=1;version=$job.version;success=$false;code='UpgradeFailed'} } catch {}
        if ($parentExited) { try { RestartApp $PSScriptRoot } catch {} }
    }
    exit 1
} finally {
    if ($commitSignal) { $commitSignal.Dispose() }
    if ($cancelSignal) { $cancelSignal.Dispose() }
}
