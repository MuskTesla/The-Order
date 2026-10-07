param()
$ErrorActionPreference='Stop'
$root=Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '.artifacts/phase7-2b/astra-material'
$identity=Get-Content -LiteralPath (Join-Path $root 'candidate-identity.json') -Raw|ConvertFrom-Json
if($identity.directory -cne 'candidate-final'){throw 'Frozen optical Test candidate required'}
foreach($file in $identity.files){
 if((Get-FileHash -LiteralPath (Join-Path $root ($identity.directory+'/win-x64/'+$file.name))).Hash -cne $file.sha256){throw 'Candidate identity changed'}
}
foreach($asset in $identity.assets){
 if((Get-FileHash -LiteralPath (Join-Path $root $asset.path)).Hash -cne $asset.sha256){throw 'Test artwork identity changed'}
}
$manifest=Get-Content -LiteralPath (Join-Path $root 'profiles/review/native-profile.json') -Raw|ConvertFrom-Json
if($manifest.ProfileKind -cne 'Test'){throw 'Explicit Test profile required'}
& (Join-Path $root 'launch.ps1') -Candidate $identity.directory -Profile review
& (Join-Path $root 'command.ps1') -Action gallery|Out-Null
& (Join-Path $root 'command.ps1') -Action glass -Value reference|Out-Null
& (Join-Path $root 'command.ps1') -Action glass -Value candidate1|Out-Null
& (Join-Path $root 'command.ps1') -Action glass -Value off|Out-Null
Write-Output 'Isolated Test Gallery opened. Enable the advanced Test switch to compare the candidates. No Production profile is used.'
