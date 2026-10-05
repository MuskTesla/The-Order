param(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Profiles,
    [Parameter(Mandatory)][string]$Covers,
    [Parameter(Mandatory)][string]$Output,
    [int]$Samples = 5,
    [int]$Rounds = 120,
    [int[]]$Counts = @(1000,5000,10000),
    [ValidateRange(0,120)][int]$Minutes = 0,
    [ValidateRange(0,10000)][int]$PaceMs = 0
)
$ErrorActionPreference = 'Stop'
foreach ($taskPath in @($Candidate,$Profiles,$Covers,$Output)) {
    if (-not [IO.Path]::IsPathFullyQualified($taskPath) -or $taskPath -notmatch '[\\/]\.artifacts[\\/]') { throw 'Explicit artifact paths required' }
}
New-Item -ItemType Directory -Force $Output | Out-Null
$taskVariables = @('THE_ORDER_NATIVE_DATA_DIR','THE_ORDER_NATIVE_PROFILE_KIND','THE_ORDER_NATIVE_FIXTURE_PROFILE','THE_ORDER_NATIVE_DIAGNOSTICS','THE_ORDER_NATIVE_PERFORMANCE_OUTPUT','THE_ORDER_NATIVE_PERFORMANCE_COVERS','THE_ORDER_NATIVE_PERFORMANCE_ROUNDS','THE_ORDER_NATIVE_PERFORMANCE_MINUTES','THE_ORDER_NATIVE_PERFORMANCE_PACE_MS','THE_ORDER_NATIVE_ONLINE_FIXTURE','THE_ORDER_NATIVE_SCENARIO')
$taskPrior = @{}
foreach ($name in $taskVariables) { $taskPrior[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$taskRuns = @()
try {
    $env:THE_ORDER_NATIVE_PROFILE_KIND = 'Test'
    $env:THE_ORDER_NATIVE_FIXTURE_PROFILE = '0'
    $env:THE_ORDER_NATIVE_DIAGNOSTICS = '1'
    $env:THE_ORDER_NATIVE_PERFORMANCE_COVERS = $Covers
    $env:THE_ORDER_NATIVE_ONLINE_FIXTURE = '0'
    $env:THE_ORDER_NATIVE_SCENARIO = '0'
    $env:THE_ORDER_NATIVE_PERFORMANCE_MINUTES = [string]$Minutes
    $env:THE_ORDER_NATIVE_PERFORMANCE_PACE_MS = [string]$PaceMs
    foreach ($count in $Counts) {
        if ($count -notin @(1000,5000,10000)) { throw 'Unknown fixture size' }
        $env:THE_ORDER_NATIVE_DATA_DIR = Join-Path $Profiles "library-$count"
        for ($sample=0; $sample -le $Samples; $sample++) {
            $taskRun = Join-Path $Output "$count-$sample"
            New-Item -ItemType Directory -Force $taskRun | Out-Null
            $env:THE_ORDER_NATIVE_PERFORMANCE_OUTPUT = $taskRun
            $env:THE_ORDER_NATIVE_PERFORMANCE_ROUNDS = if ($sample -eq $Samples) { [string]$Rounds } else { '0' }
            $taskClock = [Diagnostics.Stopwatch]::StartNew()
            $taskProcess = Start-Process -FilePath $Candidate -WorkingDirectory (Split-Path $Candidate) -PassThru -WindowStyle Hidden
            # This script is the independent watchdog; interactive tool waits remain short.
            if (-not $taskProcess.WaitForExit([Math]::Max(600000,($Minutes+10)*60000))) { throw "UI timeout pid=$($taskProcess.Id); process retained for diagnosis" }
            if ($taskProcess.ExitCode -ne 0) { throw "UI exit code=$($taskProcess.ExitCode)" }
            if (Test-Path (Join-Path $taskRun 'ui-failure.json')) { throw "UI fixture failure at $taskRun" }
            $startup = Get-Content -LiteralPath (Join-Path $taskRun 'startup.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($startup.entries -ne $count -or $startup.firstViewport.realizedContainerCount -le 0 -or $null -eq $startup.firstCoverMs) { throw "Unusable UI at $taskRun" }
            $taskRuns += [pscustomobject]@{count=$count;sample=$sample;cacheDefinition=if($sample -eq 0){'cold-ish (first process, OS caches uncontrolled)'}else{'warm (repeat process)'};warmup=($sample -eq 0);pid=$taskProcess.Id;exitCode=$taskProcess.ExitCode;processLifetimeMs=$taskClock.Elapsed.TotalMilliseconds;startup=$startup;directory=$taskRun}
            $taskRuns | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output 'ui-runs.json') -Encoding UTF8
            Write-Output "UI count=$count sample=$sample firstUsableMs=$($startup.firstUsableMs)"
        }
    }
} finally {
    foreach ($name in $taskVariables) { [Environment]::SetEnvironmentVariable($name, $taskPrior[$name], 'Process') }
}
