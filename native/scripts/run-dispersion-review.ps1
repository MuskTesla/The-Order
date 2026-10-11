param([switch]$Advanced)
$ErrorActionPreference='Stop'
$root=Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '.artifacts/phase7-2b/dispersion'
$identity=Get-Content -LiteralPath (Join-Path $root 'candidate-identity.json') -Raw|ConvertFrom-Json
if($identity.directory -cne 'candidate-dispersion'){throw 'Isolated dispersion candidate identity required'}
foreach($file in $identity.files){if((Get-FileHash -LiteralPath (Join-Path $root ($identity.directory+'/win-x64/'+$file.name))).Hash -cne $file.sha256){throw 'Review candidate changed'}}
$manifest=Get-Content -LiteralPath (Join-Path $root 'profiles/review/native-profile.json') -Raw|ConvertFrom-Json
if($manifest.ProfileKind -cne 'Test'){throw 'Explicit Test profile required'}
& (Join-Path $root 'launch.ps1') -Candidate $identity.directory -Profile review
& (Join-Path $root 'command.ps1') -Action gallery|Out-Null
& (Join-Path $root 'command.ps1') -Action glass -Value reference|Out-Null
& (Join-Path $root 'command.ps1') -Action glass -Value recommended|Out-Null
if($Advanced){& (Join-Path $root 'command.ps1') -Action glass -Value on|Out-Null}
Write-Output 'Opened isolated Test Gallery with supplied reference. Advanced material defaults OFF.'
