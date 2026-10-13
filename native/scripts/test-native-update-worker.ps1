param([Parameter(Mandatory)][string]$Plan, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith((Join-Path $repo '.artifacts\'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Fresh workspace test output required.' }
$original=[IO.File]::ReadAllBytes($Plan);$job=Get-Content -LiteralPath $Plan -Raw|ConvertFrom-Json
if ($job.profile -notlike ($repo+'\.artifacts\*') -or (Get-Content -LiteralPath (Join-Path $job.profile 'native-profile.json') -Raw|ConvertFrom-Json).ProfileKind -ne 'Test') { throw 'Synthetic task-local Test only.' }
$null=[IO.Directory]::CreateDirectory($output)
$worker=Join-Path $PSScriptRoot 'update-worker.ps1';$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$results=[Collections.Generic.List[object]]::new()
function Run([string]$Name,[scriptblock]$Mutation,[bool]$Expected) {
    $value=[Text.Encoding]::UTF8.GetString($original)|ConvertFrom-Json
    $value.workerSha256=(Get-FileHash -LiteralPath $worker).Hash
    & $Mutation $value
    [IO.File]::WriteAllText($Plan,($value|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    $info=[Diagnostics.ProcessStartInfo]::new($shell);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($arg in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$worker,'-Plan',$Plan,'-ValidateOnly')){$info.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::Start($info)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit(15000)){throw 'Validation-only worker timed out.'}
        $passed=($process.ExitCode -eq 0) -eq $Expected
        $results.Add([pscustomobject]@{name=$Name;passed=$passed;expectedSuccess=$Expected;exitCode=$process.ExitCode;stdout=$stdout.GetAwaiter().GetResult();stderr=$stderr.GetAwaiter().GetResult()})
        if(!$passed){throw ('Worker check failed: '+$Name)}
    } finally {$process.Dispose()}
}
try {
    Run 'real-public-installer-and-Test-plan-validation-only' {} $true
    Run 'tampered-hash' {param($j)$j.sha256='0'*64} $false
    Run 'wrong-size' {param($j)$j.bytes++} $false
    Run 'oversize' {param($j)$j.bytes=536870913} $false
    Run 'invalid-version' {param($j)$j.version='1.0.2-beta'} $false
    Run 'downgrade' {param($j)$j.currentVersion='99.0.0'} $false
    Run 'foreign-installer-path' {param($j)$j.installer=Join-Path (Split-Path $j.profile) 'outside.exe'} $false
    Run 'alternate-stream' {param($j)$j.installer+=':stream'} $false
    Run 'wrong-ticket-id' {param($j)$j.id='0'*32} $false
    Run 'target-overlaps-profile' {param($j)$j.target=$j.profile} $false
    Run 'target-disk-root' {param($j)$j.target=[IO.Path]::GetPathRoot($j.target)} $false
    Run 'unknown-schema' {param($j)$j.schema=99} $false
    Run 'wrong-worker-identity' {param($j)$j.workerSha256='0'*64} $false
    Run 'wrong-handoff-event' {param($j)$j.commitEvent='Local\foreign'} $false
    Run 'invalid-parent' {param($j)$j.parentPid=0} $false
    Run 'foreign-result' {param($j)$j.result=Join-Path (Split-Path $j.profile) 'outside.json'} $false
    Run 'wrong-profile-identity' {param($j)$j.profileId=[guid]::NewGuid().ToString()} $false
    Run 'wrong-root-identity' {param($j)$j.physicalRootHash='0'*64} $false
} finally {
    [IO.File]::WriteAllBytes($Plan,$original)
    $results|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
}
[ordered]@{passed=@($results|Where-Object passed).Count;failed=@($results|Where-Object {!$_.passed}).Count;setupExecuted=$false;productionAccessed=$false;shell='Windows PowerShell 5.1';scope='Actual worker script validation against the actual public installer; no install/restart'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw
