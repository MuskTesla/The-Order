param([Parameter(Mandatory)][string]$Plan, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$output=[IO.Path]::GetFullPath($OutputDirectory)
$template=Get-Content -LiteralPath $Plan -Raw|ConvertFrom-Json
if (!$output.StartsWith((Join-Path $repo '.artifacts\'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output) -or
    !$template.profile.StartsWith((Join-Path $repo '.artifacts\'),[StringComparison]::OrdinalIgnoreCase) -or
    (Get-Content -LiteralPath (Join-Path $template.profile 'native-profile.json') -Raw|ConvertFrom-Json).ProfileKind -ne 'Test') { throw 'Fresh output and synthetic task-local Test required.' }
$null=[IO.Directory]::CreateDirectory($output)
$parent=Join-Path $output 'synthetic-parent.ps1'
@'
param([string]$Plan,[string]$Worker,[string]$Mode)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
$j=Get-Content -LiteralPath $Plan -Raw|ConvertFrom-Json
$self=Get-Process -Id $PID;$j.parentPid=$PID;$j.parentStartTicks=$self.StartTime.ToUniversalTime().Ticks.ToString();$self.Dispose()
if($Mode -eq 'wrong-owner'){$j.parentStartTicks='1'+$j.parentStartTicks.Substring(1)}
[IO.File]::WriteAllText($Plan,($j|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$commit=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$j.commitEvent)
$cancel=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$j.cancelEvent)
$folder=Split-Path -LiteralPath $Plan
$args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$Worker+'" -Plan "'+$Plan+'" -ValidateOnly -ProbeHandoff'
$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $folder 'stdout.txt') -RedirectStandardError (Join-Path $folder 'stderr.txt')
$watch=[Diagnostics.Stopwatch]::StartNew()
while(!(Test-Path -LiteralPath (Join-Path $folder 'ready.json'))){if($child.HasExited -or $watch.Elapsed.TotalSeconds -gt 15){throw 'WorkerNotReady'};Start-Sleep -Milliseconds 50}
if($Mode -eq 'commit'){
    $null=$commit.Set();Start-Sleep -Milliseconds 500
    if(Test-Path -LiteralPath (Join-Path $folder 'handoff-probe.json')){throw 'AuthorizedWhileOwnerStillAlive'}
    [IO.File]::WriteAllText((Join-Path $folder 'owner-still-alive.txt'),'No authorization after commit while owner remained alive.')
} elseif($Mode -eq 'cancel'){$null=$cancel.Set();if(!$child.WaitForExit(5000)){throw 'CancelNotObserved'}}
$child.Dispose();$commit.Dispose();$cancel.Dispose()
'@ | Set-Content -LiteralPath $parent -Encoding utf8
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe';$worker=Join-Path $PSScriptRoot 'update-worker.ps1'
$results=[Collections.Generic.List[object]]::new()
foreach($mode in @('commit','cancel','uncommitted','wrong-owner')){
    $job=($template|ConvertTo-Json -Depth 5)|ConvertFrom-Json;$job.id=[guid]::NewGuid().ToString('N')
    $job.commitEvent='Local\TheOrder.Update.Commit.'+$job.id;$job.cancelEvent='Local\TheOrder.Update.Cancel.'+$job.id
    $job.workerSha256=(Get-FileHash -LiteralPath $worker).Hash
    $folder=Join-Path (Join-Path $job.profile 'updates') $job.id;$null=[IO.Directory]::CreateDirectory($folder)
    $path=Join-Path $folder 'plan.json';[IO.File]::WriteAllText($path,($job|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    $start=[Diagnostics.ProcessStartInfo]::new($shell);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach($argument in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$parent,'-Plan',$path,'-Worker',$worker,'-Mode',$mode)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{if(!$process.WaitForExit(20000)-or $process.ExitCode -ne 0){throw ('Synthetic parent failed: '+$mode)}}finally{$process.Dispose()}
    $clock=[Diagnostics.Stopwatch]::StartNew();$receipt=Join-Path $folder 'handoff-probe.json'
    while(!(Test-Path -LiteralPath $receipt)){if($clock.Elapsed.TotalSeconds -gt 8){throw ('No probe receipt: '+$mode)};Start-Sleep -Milliseconds 50}
    $value=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
    $ok=($value.authorized -eq ($mode -eq 'commit')) -and !$value.installationStarted -and ($mode -ne 'commit' -or (Test-Path -LiteralPath (Join-Path $folder 'owner-still-alive.txt')))
    $results.Add([pscustomobject]@{mode=$mode;passed=$ok;receipt=$value})
    $results|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    if(!$ok){throw ('Handoff scenario failed: '+$mode)}
}
[ordered]@{passed=$results.Count;failed=0;actualNamedEvents=$true;actualChildProcessWait=$true;setupExecuted=$false;productionAccessed=$false;scope='Actual worker validation-only probe; synthetic parent PID/creation identity, commit, cancel and unexpected exit'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw
