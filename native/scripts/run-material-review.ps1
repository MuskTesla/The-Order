param([switch]$Advanced)
$ErrorActionPreference='Stop'
$entry=Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '.artifacts/phase7-2b'
if(!(Test-Path (Join-Path $entry 'candidate-identity.json'))){throw 'Frozen Test candidate is not available'}
$id=Get-Content (Join-Path $entry 'candidate-identity.json') -Raw|ConvertFrom-Json
if($id.directory -notmatch '^candidate(?:-[a-z-]+)?$'){throw 'Invalid isolated candidate directory'}
foreach($file in $id.files){if((Get-FileHash (Join-Path $entry ($id.directory+'/win-x64/'+$file.name))).Hash -cne $file.sha256){throw 'Frozen candidate changed'}}
& (Join-Path $entry 'launch.ps1') -Candidate $id.directory -Profile review
Start-Sleep -Seconds 3
& (Join-Path $entry 'command.ps1') -Action gallery|Out-Null
if($Advanced){& (Join-Path $entry 'command.ps1') -Action glass -Value on|Out-Null}
Write-Output 'Opened isolated 384-item Test profile. Advanced material is Gallery-only; no Production authorization.'
