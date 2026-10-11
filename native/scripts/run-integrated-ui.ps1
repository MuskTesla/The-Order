param(
 [string]$Candidate,
 [string]$DotnetPath = 'D:/Projects/The-Order/.artifacts/native-phase1/tools/dotnet/dotnet.exe',
 [switch]$Diagnostics,
 [switch]$Wait,
 [string]$Run = ('experience-' + [guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts=Join-Path $repo '.artifacts/local-development-integration'
if(!$Candidate){$Candidate=Join-Path $artifacts 'candidate-release/win-x64'}
$Candidate=[IO.Path]::GetFullPath($Candidate)
if(!$Candidate.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Integration Test candidate required'}
if($Run -notmatch '^[A-Za-z0-9_-]+$'){throw 'Invalid isolated run name'}
$dir=Join-Path $artifacts "runs/$Run"
if(Test-Path -LiteralPath $dir){throw 'Run already exists; preserving it'}
$seed=Join-Path $artifacts 'test-seed'
$covers=Join-Path $artifacts 'covers'
$helper=Join-Path $artifacts 'profile-helper/UiExperienceProfile.dll'
$exe=Join-Path $Candidate 'TheOrder.WinUI.exe'
foreach($required in @($DotnetPath,$helper,$exe,(Join-Path $seed 'seed.json'),$covers)){if(!(Test-Path -LiteralPath $required)){throw "Prepared integration artifact missing: $required"}}
[IO.Directory]::CreateDirectory((Join-Path $dir 'control'))|Out-Null
$profile=Join-Path $dir 'profile'
# Clear inherited experiment flags for this child, then restore the caller's
# process environment. Neither launch mode changes global/user settings.
$prior=@{}
Get-ChildItem Env: | Where-Object { $_.Name.StartsWith('THE_ORDER_NATIVE_') -or $_.Name -in @('KGM_DATA_DIR','KGM_TEST_EXECUTABLE','DOTNET_ROOT','DOTNET_ROOT_X64') } | ForEach-Object { $prior[$_.Name]=$_.Value }
$values=@{DOTNET_ROOT=(Split-Path $DotnetPath);DOTNET_ROOT_X64=(Split-Path $DotnetPath);THE_ORDER_NATIVE_DATA_DIR=$profile;THE_ORDER_NATIVE_PROFILE_KIND='Test';THE_ORDER_NATIVE_FIXTURE_PROFILE='0';THE_ORDER_NATIVE_DIAGNOSTICS=$(if($Diagnostics){'1'}else{'0'});THE_ORDER_NATIVE_VISUAL_TEST=$(if($Diagnostics){'1'}else{'0'});THE_ORDER_NATIVE_VISUAL_DIRECTION='B';THE_ORDER_NATIVE_ADVANCED_TEST='1';THE_ORDER_NATIVE_VISUAL_CONTROL=(Join-Path $dir 'control');THE_ORDER_NATIVE_PERFORMANCE_COVERS=$covers;THE_ORDER_NATIVE_ONLINE_FIXTURE='0';THE_ORDER_NATIVE_MATERIAL_SOURCE='owned-scene'}
$app=$null
try{
 foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
 foreach($name in $values.Keys){[Environment]::SetEnvironmentVariable($name,$values[$name],'Process')}
 & $DotnetPath $helper $seed $profile | Set-Content -LiteralPath (Join-Path $dir 'profile.json') -Encoding utf8
 if($LASTEXITCODE -ne 0){throw 'Isolated Test profile preparation failed'}
 if((Get-Content -LiteralPath (Join-Path $profile 'native-profile.json') -Raw|ConvertFrom-Json).ProfileKind -ne 'Test'){throw 'Test profile guard failed'}
 $app=Start-Process -FilePath $exe -WorkingDirectory $Candidate -WindowStyle Normal -PassThru -RedirectStandardOutput (Join-Path $dir 'stdout.log') -RedirectStandardError (Join-Path $dir 'stderr.log')
 $identity=@{pid=$app.Id;start=$app.StartTime.ToUniversalTime().ToString('o');candidate=$exe;profile=$profile;diagnostics=[bool]$Diagnostics;dll=(Get-FileHash -LiteralPath (Join-Path $Candidate 'TheOrder.WinUI.dll')).Hash;optical=(Get-FileHash -LiteralPath (Join-Path $Candidate 'TheOrder.OpticalMaterial.dll')).Hash;libraryBefore=(Get-FileHash -LiteralPath (Join-Path $profile 'library.json')).Hash}
 $identity|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'identity.json') -Encoding utf8
 $identity|ConvertTo-Json
}finally{
 foreach($name in $values.Keys){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
 foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
}
if($Wait -and $app){
 try{
  $app.WaitForExit()
  @{exitCode=$app.ExitCode;ended=[DateTimeOffset]::UtcNow.ToString('o');libraryAfter=(Get-FileHash -LiteralPath (Join-Path $profile 'library.json')).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'exit.json') -Encoding utf8
 }finally{$app.Dispose()}
}elseif($app){$app.Dispose()}
