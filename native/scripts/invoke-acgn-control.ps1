param(
    [Parameter(Mandatory)][string]$ControlDirectory,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [Parameter(Mandatory)][ValidateSet('inspect','focus','invoke','expand','collapse','toggle','select0','select1','select2')][string]$Mode,
    [Parameter(Mandatory)][ValidateLength(1,200)][string]$Id
)
$ErrorActionPreference='Stop'
foreach($path in @($ControlDirectory,$ProfileDirectory)) {
    if(![IO.Path]::IsPathFullyQualified($path) -or [IO.Path]::GetFullPath($path) -notmatch '[\\/]\.artifacts[\\/]') {throw 'Explicit isolated artifact paths required'}
}
$ControlDirectory=[IO.Path]::GetFullPath($ControlDirectory)
$ProfileDirectory=[IO.Path]::GetFullPath($ProfileDirectory)
$profile=Get-Content -LiteralPath (Join-Path $ProfileDirectory 'native-profile.json') -Raw|ConvertFrom-Json
if($profile.ProfileKind -ne 'Test'){throw 'Marked Test profile required'}
$run=Get-Content -LiteralPath (Join-Path $ControlDirectory 'process.json') -Raw|ConvertFrom-Json
if([IO.Path]::GetFullPath($run.profile) -ne $ProfileDirectory){throw 'Test process/Profile mismatch'}
$process=Get-Process -Id $run.pid
if([IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath($run.exe)){throw 'Test PID/executable mismatch'}
if(!(Test-Path -LiteralPath (Join-Path $ControlDirectory 'fixture-ready.json'))){throw 'Test controller not ready'}
$nonce=[guid]::NewGuid().ToString('N')
$temporary=Join-Path $ControlDirectory ($nonce+'.tmp')
@{nonce=$nonce;action='acgnControl';value=$Mode;id=$Id}|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding utf8
[IO.File]::Move($temporary,(Join-Path $ControlDirectory 'request.json'),$true)
$deadline=[DateTime]::UtcNow.AddSeconds(25)
do {
    Start-Sleep -Milliseconds 100
    $file=Join-Path $ControlDirectory 'receipt.json'
    if(!(Test-Path -LiteralPath $file)){continue}
    try{$receipt=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json}catch{continue}
    if($receipt.nonce -ne $nonce){continue}
    $receipt|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $ControlDirectory ('acgn-'+$nonce+'.json')) -Encoding utf8
    if(!$receipt.success){throw ('Native metadata control failed: '+$receipt.error)}
    $evidence=Get-Content -LiteralPath (Join-Path $ControlDirectory 'acgn-control-evidence.json') -Raw|ConvertFrom-Json
    $evidence|ConvertTo-Json -Depth 40|Set-Content -LiteralPath (Join-Path $ControlDirectory ('control-'+$nonce+'.json')) -Encoding utf8
    Write-Output ($Mode+' '+$Id+' completed; real WinUI automation peer')
    return
}while([DateTime]::UtcNow -lt $deadline)
throw 'Native metadata control timed out; process and evidence retained'
