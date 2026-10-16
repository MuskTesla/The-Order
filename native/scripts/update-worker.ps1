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
    $null = PlainPath $Path
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 8))
    $file = [IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,$null) } else { [IO.File]::Move($temporary,$Path) }
}
function ReleaseVersion([string]$Text) {
    if ($Text -notmatch '\A((0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4}))(-beta\.([1-9][0-9]{0,4}))?\z') { throw 'InvalidVersion' }
    $core = [version]$Matches[1]; $beta = $null
    if ($Matches[6]) { $beta = [int]$Matches[6] }
    if ($core.Major -gt 65535 -or $core.Minor -gt 65535 -or $core.Build -gt 65535 -or $beta -gt 65535) { throw 'InvalidVersion' }
    $revision = if ($null -eq $beta) { 0 } else { $beta }
    return [pscustomobject]@{core=$core;beta=$beta;file=($core.ToString(3)+'.'+$revision);text=$Text}
}
function CanApply($From,$To,[int]$Protocol) {
    $same = ($null -eq $From.beta) -eq ($null -eq $To.beta)
    $compare = $To.core.CompareTo($From.core)
    if ($compare -eq 0) {
        if ($null -eq $To.beta) { $compare = if ($null -eq $From.beta) { 0 } else { 1 } }
        elseif ($null -eq $From.beta) { $compare = -1 }
        else { $compare = $To.beta.CompareTo($From.beta) }
    }
    return ($compare -gt 0 -and ($same -or $Protocol -eq 1)) -or ($null -ne $From.beta -and $null -eq $To.beta -and $Protocol -eq 1)
}
function SnapshotProfile($Job) {
    # Caller holds the native writer lease after the exact owner has saved/exited.
    $root = PlainPath $Job.profile
    $snapshot = PlainPath (Join-Path (Join-Path $root 'channel-backups') $Job.id)
    if (Test-Path -LiteralPath $snapshot) { throw 'SnapshotAlreadyExists' }
    [IO.Directory]::CreateDirectory($snapshot) | Out-Null
    $data = Join-Path $snapshot 'data'; [IO.Directory]::CreateDirectory($data) | Out-Null
    $pending = New-Object 'Collections.Generic.Stack[string]'; $pending.Push($root)
    $records = New-Object 'Collections.Generic.List[object]'; [long]$total = 0
    $excluded = @('updates','channel-backups','WebView2','webview2','PVWebView2','logs',
        'native-bangumi-relations-v1','native-bangumi-subjects-v1','native-anilist-v1','native-musicbrainz-v1',
        'native-book-bibliography-v1','native-watch-snapshots-v1')
    while ($pending.Count -gt 0) {
        $folder = $pending.Pop(); $null = PlainPath $folder
        foreach ($entry in [IO.Directory]::GetFileSystemEntries($folder)) {
            $null = PlainPath $entry; $name = [IO.Path]::GetFileName($entry)
            if ((Same $folder $root) -and ($name -in $excluded -or $name -eq '.the-order-writer.lock')) { continue }
            if ([IO.Directory]::Exists($entry)) { $pending.Push($entry); continue }
            $info = [IO.FileInfo]::new($entry); $total += $info.Length
            if ($total -gt 2147483648 -or $records.Count -ge 8192) { throw 'SnapshotLimit' }
            $relative = $entry.Substring($root.Length + 1)
            $target = PlainPath (Join-Path $data $relative)
            if (!$target.StartsWith($data+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'SnapshotBoundary' }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $input = [IO.File]::Open($entry,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $output = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
                $hash = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash
                if ($hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw 'SnapshotHash' }
                $records.Add(@{path=$relative;bytes=$info.Length;sha256=$hash})
            } finally { $input.Dispose() }
        }
    }
    WriteJson (Join-Path $snapshot 'snapshot.json') @{schema=1;state='Ready';profileId=$Job.profileId;storeId=$Job.storeId;
        physicalRoot=$Job.physicalRootHash;fromVersion=$Job.currentVersion;toVersion=$Job.version;files=@($records.ToArray());bytes=$total}
    return $snapshot
}
function InstallerValid($Job) {
    $path=PlainPath $Job.installer
    if ((Get-Item -LiteralPath $path).Length -ne $Job.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Job.sha256) { throw 'InstallerHash' }
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $offered = ReleaseVersion $Job.version
    if ($version.ProductName.Trim() -ne 'The Order' -or ([version]$version.FileVersion.Trim()).ToString() -ne $offered.file -or $version.ProductVersion.Trim() -ne $Job.version) { throw 'InstallerIdentity' }
}
function LoadJob([string]$Path,[bool]$OnlyValidate) {
    $path=PlainPath $Path
    if ([IO.Path]::GetFileName($path) -ne 'plan.json' -or (Get-Item -LiteralPath $path).Length -gt 16384) { throw 'InvalidPlan' }
    $job=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if ($job.schema -notin @(1,2) -or $job.id -notmatch '^[a-f0-9]{32}$' -or $job.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $job.workerSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'InvalidIdentity' }
    $version=ReleaseVersion $job.version; $current=ReleaseVersion $job.currentVersion
    $protocol = if ($job.schema -eq 2) { [int]$job.channelProtocol } else { 0 }
    if (!(CanApply $current $version $protocol) -or ($job.schema -eq 1 -and ($null -ne $version.beta -or $null -ne $current.beta)) -or $job.bytes -lt 2 -or $job.bytes -gt 536870912) { throw 'InvalidVersion' }
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
    if ($job.schema -eq 2 -and $job.profileKind -ne $stored.ProfileKind) { throw 'ProfileIdentity' }
    $ownedFormal = ($stored.ProfileKind -eq 'Production' -and (Same $profile (Join-Path $env:LOCALAPPDATA 'TheOrder\Profiles\Production'))) -or
        ($stored.ProfileKind -eq 'Beta' -and (Same $profile (Join-Path $env:LOCALAPPDATA 'TheOrder-Beta\Profiles\Beta')))
    if (!$OnlyValidate -and !$ownedFormal) { throw 'ProductionOnly' }
    if ($OnlyValidate -and $stored.ProfileKind -notin @('Test','Production','Beta')) { throw 'ProfileIdentity' }
    $target=PlainPath $job.target
    foreach ($protected in @($profile,(Join-Path $env:LOCALAPPDATA 'TheOrder'),(Join-Path $env:LOCALAPPDATA 'TheOrder-Beta'),(Join-Path $env:APPDATA 'KunGalManager'))) {
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
$job=$null; $parentExited=$false; $commitSignal=$null; $cancelSignal=$null; $profileLease=$null
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
    $running=@(Get-CimInstance Win32_Process -Filter "Name='TheOrder.WinUI.exe'" | Where-Object { !$_.ExecutablePath -or (Same ([IO.Path]::GetDirectoryName($_.ExecutablePath)) $job.target) })
    if ($running) { throw 'AnotherInstanceRunning' }
    $profileLease=[IO.File]::Open((Join-Path $job.profile '.the-order-writer.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $snapshot=SnapshotProfile $job
    $lock=[IO.File]::Open($job.installer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        InstallerValid $job
        $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="'+$job.target+'" /LOG="'+(Join-Path $folder 'install.log')+'"'
        $setup=Start-Process -FilePath $job.installer -ArgumentList $arguments -WorkingDirectory $folder -PassThru -Wait -WindowStyle Hidden
        try { if ($setup.ExitCode -ne 0) { throw 'SetupFailed' } } finally { $setup.Dispose() }
    } finally { $lock.Dispose() }
    $app=Join-Path $job.target 'TheOrder.WinUI.exe'; $null=PlainPath $app
    $installed=[Diagnostics.FileVersionInfo]::GetVersionInfo($app)
    if ($installed.ProductVersion.Trim() -ne $job.version -or $installed.FileVersion.Trim() -ne (ReleaseVersion $job.version).file) { throw 'InstalledVersionMismatch' }
    WriteJson (Join-Path $job.target 'active-profile.json') @{Version=1;Kind=$(if($job.schema -eq 2){[int]$(if($job.profileKind -eq 'Beta'){3}else{2})}else{2});ProfileId=$job.profileId;StoreId=$job.storeId}
    WriteJson $job.result @{schema=1;version=$job.version;success=$true;code='Installed';snapshot=$snapshot}
    $profileLease.Dispose(); $profileLease=$null
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
        if ($profileLease) { $profileLease.Dispose(); $profileLease=$null }
        if ($parentExited) { try { RestartApp $PSScriptRoot } catch {} }
    }
    exit 1
} finally {
    if ($commitSignal) { $commitSignal.Dispose() }
    if ($cancelSignal) { $cancelSignal.Dispose() }
    if ($profileLease) { $profileLease.Dispose() }
}
