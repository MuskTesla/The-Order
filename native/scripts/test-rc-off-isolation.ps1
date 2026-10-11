param([Parameter(Mandatory)][string]$Candidate, [Parameter(Mandatory)][string]$EvidenceRoot, [Parameter(Mandatory)][string]$DotnetPath, [Parameter(Mandatory)][string]$Covers)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
if(!$evidence.StartsWith((Join-Path $repo '.artifacts/'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $evidence)){throw 'Fresh workspace evidence required'}
[IO.Directory]::CreateDirectory($evidence)|Out-Null
$seed=Join-Path $repo '.artifacts/local-development-integration/test-seed'
$helper=Join-Path $repo '.artifacts/local-development-integration/profile-helper/UiExperienceProfile.dll'
$ids=@((Get-Content -LiteralPath (Join-Path $seed 'library.payload.json') -Raw|ConvertFrom-Json).games | Select-Object -First 24 -ExpandProperty id)
$protocol=Join-Path $repo 'docs/ai/PHASE7_FINALIZATION.md'
@{protocolHash=(Get-FileHash $protocol).Hash;appHash=(Get-FileHash (Join-Path $Candidate 'TheOrder.WinUI.dll')).Hash;opticalHash=(Get-FileHash (Join-Path $Candidate 'TheOrder.OpticalMaterial.dll')).Hash;noForcedGc=$true;diagnosticPolling=$false;items=384;tripsPerSegment=24;warmupSeconds=30;idleSeconds=30;peakIncrementBudgetMiB=64;retentionBudgetMiB=64}|ConvertTo-Json|Set-Content "$evidence/protocol-identity.json"
$total=[Diagnostics.Stopwatch]::StartNew()
foreach($arm in @('cold-standard','advanced-to-standard')){
 $dir=Join-Path $evidence $arm;[IO.Directory]::CreateDirectory($dir)|Out-Null
 $profile=Join-Path $repo ('.artifacts/local-development-integration/runs/rc-'+$arm+'-'+[guid]::NewGuid().ToString('N')+'/profile')
 & $DotnetPath $helper $seed $profile|Set-Content "$dir/profile-preparation.json";if($LASTEXITCODE){throw 'Synthetic profile preparation failed'}
 $control=Join-Path $dir 'control';[IO.Directory]::CreateDirectory($control)|Out-Null
 $prior=@{};Get-ChildItem Env:|Where-Object {$_.Name.StartsWith('THE_ORDER_NATIVE_') -or $_.Name -in @('KGM_DATA_DIR','KGM_TEST_EXECUTABLE','DOTNET_ROOT','DOTNET_ROOT_X64')}|ForEach-Object{$prior[$_.Name]=$_.Value}
 $values=@{THE_ORDER_NATIVE_DATA_DIR=$profile;THE_ORDER_NATIVE_PROFILE_KIND='Test';THE_ORDER_NATIVE_DIAGNOSTICS='0';THE_ORDER_NATIVE_VISUAL_TEST='1';THE_ORDER_NATIVE_ADVANCED_TEST=$(if($arm -eq 'cold-standard'){'0'}else{'1'});THE_ORDER_NATIVE_VISUAL_CONTROL=$control;THE_ORDER_NATIVE_PERFORMANCE_COVERS=$Covers}
 $app=$null;$sampler=$null;$stop=Join-Path $dir 'sampler.stop';$stream=[IO.StreamWriter]::new("$dir/boundaries.jsonl",$false,[Text.UTF8Encoding]::new($false));$completed=$false
 try{
  foreach($n in $prior.Keys){[Environment]::SetEnvironmentVariable($n,$null,'Process')};foreach($n in $values.Keys){[Environment]::SetEnvironmentVariable($n,$values[$n],'Process')}
  $exe=Join-Path $Candidate 'TheOrder.WinUI.exe';$app=Start-Process $exe -WorkingDirectory $Candidate -PassThru
  $identity=@{pid=$app.Id;start=$app.StartTime.ToUniversalTime().ToString('o');candidate=$exe;profile=$profile;control=$control;libraryBefore=(Get-FileHash (Join-Path $profile 'library.json')).Hash}
  $identity|ConvertTo-Json|Set-Content "$dir/identity.json"
  foreach($n in $values.Keys){[Environment]::SetEnvironmentVariable($n,$null,'Process')};foreach($n in $prior.Keys){[Environment]::SetEnvironmentVariable($n,$prior[$n],'Process')}
  $sampler=Start-Job -ArgumentList $app.Id,$identity.start,"$dir/process.jsonl",$stop -ScriptBlock {
   param($targetPid,$generation,$file,$stopFile)
   $out=[IO.StreamWriter]::new($file,$false,[Text.UTF8Encoding]::new($false))
   try{while(!(Test-Path -LiteralPath $stopFile)){
    try{$p=Get-Process -Id $targetPid -ErrorAction Stop;if($p.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$generation).UtcTicks){break};$utc=[DateTimeOffset]::UtcNow
    $out.WriteLine((@{utc=$utc.ToString('o');processAgeSeconds=($utc-[DateTimeOffset]$generation).TotalSeconds;privateBytes=$p.PrivateMemorySize64;workingSet=$p.WorkingSet64;handles=$p.HandleCount}|ConvertTo-Json -Compress));$out.Flush();$p.Dispose()}catch{break};Start-Sleep -Milliseconds 1000
   }}finally{$out.Dispose()}
  }
  function Cmd($action,$value=$null,$game=''){
   if($total.Elapsed.TotalMinutes -gt 30){throw 'Resource workload budget exhausted'}
   $app.Refresh();if($app.HasExited){throw 'Test exited unexpectedly'}
   if($app.PrivateMemorySize64 -gt 1.5GB){throw 'Private protection limit'}
   $nonce=[guid]::NewGuid().ToString('N');$req=@{nonce=$nonce;action=$action};if($null -ne $value){$req.value=$value};if($game){$req.id=$game}
   $req|ConvertTo-Json -Depth 5|Set-Content "$control/next.tmp" -Encoding utf8;Move-Item "$control/next.tmp" "$control/request.json" -Force
   if($action -eq 'exit'){return}
   $watch=[Diagnostics.Stopwatch]::StartNew();do{Start-Sleep -Milliseconds 80;try{$r=Get-Content "$control/receipt.json" -Raw|ConvertFrom-Json}catch{$r=$null}}while((!$r -or $r.nonce -ne $nonce) -and $watch.Elapsed.TotalSeconds -lt 15)
   if(!$r -or $r.nonce -ne $nonce -or !$r.success){throw "Test command failed: $action"};return $r
  }
  function Boundary($name){
   $r=Cmd probe;$app.Refresh();$e=$r.evidence;$runtime=$r.runtime.Substring('THE_ORDER_RUNTIME '.Length)|ConvertFrom-Json
   $row=@{name=$name;utc=[DateTimeOffset]::UtcNow.ToString('o');processAgeSeconds=([DateTimeOffset]::UtcNow-[DateTimeOffset]$identity.start).TotalSeconds;privateBytes=$app.PrivateMemorySize64;workingSet=$app.WorkingSet64;handles=$app.HandleCount;evidence=$e;images=$runtime.imageMetrics}
   $stream.WriteLine(($row|ConvertTo-Json -Depth 30 -Compress));$stream.Flush()
   if($e.diagnosticsPolling -or $e.pendingNavigation -or $e.pendingDetails -or $e.staleCovers -ne 0){throw 'Boundary correctness failure'}
   foreach($stage in @($e.stages.library.stage,$e.stages.details.stage)){if($stage.rendererActive -or $stage.retirementPending -or $stage.sourceBytes -ne 0 -or $stage.frostBytes -ne 0 -or $stage.failure -or $stage.renderersCreated -ne $stage.renderersReleased){throw 'OFF owner not retired'}}
   return $row
  }
  Start-Sleep -Seconds 3;Cmd theme Dark|Out-Null;Cmd layout grid|Out-Null
  if($arm -eq 'advanced-to-standard'){
   Cmd materialMode Advanced|Out-Null;Cmd page Details $ids[0]|Out-Null
   $ready=Cmd probe;if($ready.evidence.stages.details.stage.effective -ne 'Glass' -or !$ready.evidence.stages.details.stage.rendererActive){throw 'Controlled C1 not active'}
   $ready|ConvertTo-Json -Depth 30|Set-Content "$dir/advanced-confirmed.json"
   Cmd materialMode Standard|Out-Null
  }
  Cmd page Library|Out-Null;Cmd scroll 0|Out-Null
  $warm=[Diagnostics.Stopwatch]::StartNew();for($i=0;$i -lt 8;$i++){Cmd page Details $ids[$i]|Out-Null;Cmd page Library|Out-Null}
  while($warm.Elapsed.TotalSeconds -lt 30){Start-Sleep -Milliseconds 500}
  $start=Boundary warmup
  for($segment=1;$segment -le 2;$segment++){
   for($i=0;$i -lt 24;$i++){$cycle=[Diagnostics.Stopwatch]::StartNew();Cmd page Details $ids[$i]|Out-Null;Cmd page Library|Out-Null;Cmd scroll 0|Out-Null;while($cycle.Elapsed.TotalSeconds -lt 3){Start-Sleep -Milliseconds 100}}
   Boundary "segment$segment-end"|Out-Null
   $idle=[Diagnostics.Stopwatch]::StartNew();while($idle.Elapsed.TotalSeconds -lt 30){Start-Sleep -Milliseconds 500}
   $last=Boundary "idle$segment-end"
   foreach($name in @('sceneRevisions','draws','frostPasses')){foreach($place in @('library','details')){if($last.evidence.stages.$place.stage.$name -ne $start.evidence.stages.$place.stage.$name){throw "OFF advanced work: $place/$name"}}}
  }
  Cmd hide|Out-Null;Boundary hidden|Out-Null;Cmd show|Out-Null;Boundary restored|Out-Null
  $modules=@($app.Modules|Where-Object ModuleName -Like 'TheOrder.*Material*'|Select-Object -ExpandProperty ModuleName)
  if($arm -eq 'cold-standard' -and $modules.Count){throw 'Cold Standard loaded an optical DLL'}
  Cmd exit
  if(!$app.WaitForExit(15000) -or $app.ExitCode -ne 0){throw 'Normal exit failed'}
  $after=(Get-FileHash (Join-Path $profile 'library.json')).Hash;if($after -ne $identity.libraryBefore){throw 'Test library changed'}
  @{status='CORRECTNESS_PASS_TREND_PENDING';exitCode=$app.ExitCode;trips=48;warmupTrips=8;modules=$modules;libraryBefore=$identity.libraryBefore;libraryAfter=$after;final=$last}|ConvertTo-Json -Depth 30|Set-Content "$dir/completion.json"
  $completed=$true;Write-Output "$arm completed"
 }catch{@{status='FAIL';error=$_.Exception.Message}|ConvertTo-Json|Set-Content "$dir/failure.json";throw}
 finally{
  foreach($n in $values.Keys){[Environment]::SetEnvironmentVariable($n,$null,'Process')};foreach($n in $prior.Keys){[Environment]::SetEnvironmentVariable($n,$prior[$n],'Process')}
  if(!$completed -and $app -and !$app.HasExited){try{Cmd exit;$app.WaitForExit(15000)|Out-Null}catch{}}
  Set-Content $stop 'stop';if($sampler){Wait-Job $sampler -Timeout 5|Out-Null;Receive-Job $sampler|Set-Content "$dir/sampler-output.txt";Remove-Job $sampler -Force};$stream.Dispose();if($app){$app.Dispose()}
 }
}
@{resourceSeconds=$total.Elapsed.TotalSeconds;budgetMinutes=30}|ConvertTo-Json|Set-Content "$evidence/duration.json"
