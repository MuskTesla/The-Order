param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$ExpectedExecutable,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(1,60)][int]$IntervalSeconds = 30,
    [ValidateRange(1,180)][int]$MaximumMinutes = 140
)
$ErrorActionPreference = 'Stop'
foreach ($taskPath in @($ExpectedExecutable,$Output)) {
    if (-not [IO.Path]::IsPathFullyQualified($taskPath) -or $taskPath -notmatch '[\\/]\.artifacts[\\/]') { throw 'Artifact paths required' }
}
Add-Type 'using System; using System.Runtime.InteropServices; public static class TheOrderPerformanceGui { [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags); }'
New-Item -ItemType Directory -Force $Output | Out-Null
$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskStart = $null
$taskSamples = @()
$taskStatus = 'WATCHDOG_LIMIT'
while ($taskClock.Elapsed.TotalMinutes -lt $MaximumMinutes) {
    $taskProcess = $null
    try {
        try { $taskProcess = [Diagnostics.Process]::GetProcessById($ProcessId) } catch [ArgumentException] { $taskStatus = 'PROCESS_EXITED'; break }
        if ($taskProcess.MainModule.FileName -cne $ExpectedExecutable) { throw 'Executable identity changed' }
        if ($null -eq $taskStart) { $taskStart = $taskProcess.StartTime.ToUniversalTime().ToString('o') }
        if ($taskStart -cne $taskProcess.StartTime.ToUniversalTime().ToString('o')) { throw 'Process generation changed' }
        $taskSample = [ordered]@{at=(Get-Date).ToString('o');seconds=$taskClock.Elapsed.TotalSeconds;pid=$ProcessId;start=$taskStart;handles=$taskProcess.HandleCount;threads=$taskProcess.Threads.Count;workingSet=$taskProcess.WorkingSet64;privateBytes=$taskProcess.PrivateMemorySize64;cpuMs=$taskProcess.TotalProcessorTime.TotalMilliseconds;user=[TheOrderPerformanceGui]::GetGuiResources($taskProcess.Handle,1);gdi=[TheOrderPerformanceGui]::GetGuiResources($taskProcess.Handle,0)}
        $taskSamples += $taskSample
        $taskSample | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $Output 'samples.jsonl') -Encoding UTF8
        $taskSample | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'progress.json') -Encoding UTF8
    } catch {
        # Exit may race between locating the process and reading a counter.
        if ($taskProcess -and $taskProcess.HasExited) { $taskStatus = 'PROCESS_EXITED'; break }
        throw
    } finally { if ($taskProcess) { $taskProcess.Dispose() } }
    Start-Sleep -Seconds $IntervalSeconds
}
[ordered]@{status=$taskStatus;pid=$ProcessId;start=$taskStart;executable=$ExpectedExecutable;durationSeconds=$taskClock.Elapsed.TotalSeconds;samples=$taskSamples;productionAccess=$false;note='External bounded sampler; checks executable path and process creation generation, closes each Process handle. Does not enumerate individual file handles or CLR timers.'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding UTF8
