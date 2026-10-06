param([ValidateSet('A','B')][string]$Direction='B', [ValidateSet('baseline','round1b','round2','final','review')][string]$Candidate='review', [ValidateSet('library','empty','installations','readonly')][string]$TestProfile='library')
$ErrorActionPreference='Stop'
$visualRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$visualArtifacts = Join-Path $visualRoot '.artifacts/phase7-2a'
$visualExe = Join-Path $visualArtifacts "$Candidate/win-x64/TheOrder.WinUI.exe"
if (!(Test-Path -LiteralPath $visualExe)) { throw 'Visual candidate is not published.' }
$visualEnvironmentNames=@('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_CLI_HOME','THE_ORDER_NATIVE_DATA_DIR','THE_ORDER_NATIVE_PROFILE_KIND','THE_ORDER_NATIVE_FIXTURE_PROFILE','THE_ORDER_NATIVE_DIAGNOSTICS','THE_ORDER_NATIVE_VISUAL_TEST','THE_ORDER_NATIVE_VISUAL_DIRECTION','THE_ORDER_NATIVE_VISUAL_CONTROL','THE_ORDER_NATIVE_PERFORMANCE_COVERS','THE_ORDER_NATIVE_ONLINE_FIXTURE','THE_ORDER_NATIVE_PERFORMANCE_OUTPUT')
$visualEnvironmentBefore=@{}
foreach($visualEnvironmentName in $visualEnvironmentNames){$visualEnvironmentBefore[$visualEnvironmentName]=[Environment]::GetEnvironmentVariable($visualEnvironmentName,'Process')}
$visualProcess=$null
try {
$env:DOTNET_ROOT=Join-Path $visualRoot '.artifacts/native-phase1/tools/dotnet'
$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
$env:DOTNET_CLI_HOME=Join-Path $visualArtifacts 'dotnet-home'
$env:THE_ORDER_NATIVE_DATA_DIR=Join-Path $visualArtifacts $(switch($TestProfile){'empty'{'profile-empty'} 'installations'{'profile-installations'} 'readonly'{'raw-readonly-fixture'} default{'profile'}})
$env:THE_ORDER_NATIVE_PROFILE_KIND='Test'
# The existing raw tooling entry is used only for the explicitly selected future-
# Session read-only fixture. It is not an authoritative Native Production store.
$env:THE_ORDER_NATIVE_FIXTURE_PROFILE=if($TestProfile -eq 'readonly'){'1'}else{'0'}
$env:THE_ORDER_NATIVE_DIAGNOSTICS='1'
$env:THE_ORDER_NATIVE_VISUAL_TEST='1'
$env:THE_ORDER_NATIVE_VISUAL_DIRECTION=$Direction
$env:THE_ORDER_NATIVE_VISUAL_CONTROL=Join-Path $visualArtifacts 'control'
[System.IO.Directory]::CreateDirectory($env:THE_ORDER_NATIVE_VISUAL_CONTROL) | Out-Null
@{nonce=[guid]::NewGuid().ToString('N');action='probe'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:THE_ORDER_NATIVE_VISUAL_CONTROL 'request.json') -Encoding utf8
$env:THE_ORDER_NATIVE_PERFORMANCE_COVERS=Join-Path $visualArtifacts 'covers'
$env:THE_ORDER_NATIVE_ONLINE_FIXTURE='1'
Remove-Item Env:THE_ORDER_NATIVE_PERFORMANCE_OUTPUT -ErrorAction SilentlyContinue
$visualRun=[guid]::NewGuid().ToString('N')
$visualProcess=Start-Process -FilePath $visualExe -WorkingDirectory (Split-Path $visualExe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $visualArtifacts "tools/start-$visualRun.out.txt") -RedirectStandardError (Join-Path $visualArtifacts "tools/start-$visualRun.err.txt") -PassThru
[pscustomobject]@{Pid=$visualProcess.Id;Candidate=$visualExe;Direction=$Direction;Profile=$env:THE_ORDER_NATIVE_DATA_DIR;StartedAtUtc=[DateTimeOffset]::UtcNow} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $visualArtifacts 'candidate-process.json') -Encoding utf8
Write-Output "Visual $Direction Test candidate PID $($visualProcess.Id)"
} finally {
    if($null -ne $visualProcess){$visualProcess.Dispose()}
    foreach($visualEnvironmentName in $visualEnvironmentNames){[Environment]::SetEnvironmentVariable($visualEnvironmentName,$visualEnvironmentBefore[$visualEnvironmentName],'Process')}
}
