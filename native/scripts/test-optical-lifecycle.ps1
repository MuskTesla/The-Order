param([Parameter(Mandatory)][string]$EvidenceRoot,[string]$Candidate='candidate-final',[int]$DurationSeconds=720)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($EvidenceRoot)
if(!$root.Contains('\.artifacts\phase7-2b\') -or $DurationSeconds -lt 600 -or $DurationSeconds -gt 900){throw 'Bounded Test evidence path and 10-15 minute duration required'}
$owned=& "$root/launch.ps1" -Candidate $Candidate -Profile short -ReturnProcess
$clock=[Diagnostics.Stopwatch]::StartNew();$actions=0;$stage='lifecycle'
$stream=[IO.StreamWriter]::new("$root/lifecycle.jsonl",$false,[Text.UTF8Encoding]::new($false))
function Cmd($action,$value=''){
 $receipt=& "$root/command.ps1" -Action $action -Value $value | ConvertFrom-Json
 $script:actions++;$owned.Refresh();$g=$receipt.evidence.gallery
 if($g -and ($g.Failure -or $g.CleanupFailures -ne 0 -or $g.RetainedFailedResources -ne 0 -or $g.Optical.RetainedFailedDevice -ne 0 -or $g.GalleryCoverFailures -ne 0)){throw "Material failure: $action/$value"}
 $stream.WriteLine((@{seconds=$clock.Elapsed.TotalSeconds;stage=$script:stage;action=$action;value=$value;privateBytes=$owned.PrivateMemorySize64;workingSet=$owned.WorkingSet64;handles=$owned.HandleCount;cpuMs=$owned.TotalProcessorTime.TotalMilliseconds;evidence=$receipt.evidence}|ConvertTo-Json -Depth 20 -Compress));$stream.Flush()
 return $receipt
}
function CheckOff($r){$g=$r.evidence.gallery;if($g.SurfaceCount -ne 0 -or $g.OwnedDeviceCount -ne 0 -or $g.Optical.RenderingSubscription -ne 0 -or $g.EventSubscriptionCount -ne 0){throw 'Off/unload retained active resources'}}
function Ready{
 $deadline=[DateTime]::UtcNow.AddSeconds(8)
 do{$r=Cmd probe;if($r.evidence.gallery.Optical.Active -and !$r.evidence.gallery.Optical.SnapshotPending){return $r};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 throw 'Optical source readiness timeout'
}
function WaitTo($seconds){while($clock.Elapsed.TotalSeconds -lt $seconds){Start-Sleep -Milliseconds ([int][Math]::Min(5000,[Math]::Max(1,($seconds-$clock.Elapsed.TotalSeconds)*1000)));Cmd probe|Out-Null}}
try{
 Cmd gallery|Out-Null;CheckOff (Cmd glass off)
 for($i=0;$i -lt 18;$i++){
  $mode=if($i%2){'candidate2'}else{'candidate1'}
  Cmd glass on|Out-Null;Cmd glass $mode|Out-Null;Cmd glass $(if($i%2){'artDark'}else{'reference'})|Out-Null;Ready|Out-Null
  Cmd glass fastMotion|Out-Null;Cmd motion true|Out-Null
  $r=Cmd probe;if($r.evidence.gallery.Optical.RenderingSubscription -ne 0){throw 'Reduced motion kept rendering'}
  Cmd motion false|Out-Null;Cmd theme $(if($i%2){'Light'}else{'Dark'})|Out-Null
  foreach($width in @(480,1160)){& "$root/command.ps1" -Action resize -Width $width -Height 820|Out-Null;Ready|Out-Null}
  CheckOff (Cmd hide);Cmd show|Out-Null;Ready|Out-Null
  CheckOff (Cmd page Library);Cmd gallery|Out-Null;Ready|Out-Null
  foreach($policy in @('unknown','highContrast')){CheckOff (Cmd glass $policy);Cmd glass normal|Out-Null;Ready|Out-Null}
  CheckOff (Cmd solid true);Cmd solid false|Out-Null;Ready|Out-Null
  CheckOff (Cmd glass off);Cmd glass on|Out-Null;Ready|Out-Null
  Cmd galleryNew|Out-Null;Cmd glass $mode|Out-Null;Ready|Out-Null
  Cmd glass slowMotion|Out-Null;Start-Sleep -Milliseconds 3200
  $end=Cmd probe
  if($end.evidence.gallery.Optical.RenderingSubscription -ne 0){throw 'Finite animation did not stop'}
  Write-Output "Lifecycle cycle $($i+1)/18, $([int]$clock.Elapsed.TotalSeconds)s"
 }
 $stage='advanced-idle'
 Cmd glass reference|Out-Null;Cmd galleryStep 0|Out-Null;$initial=Ready
 $draws=$initial.evidence.gallery.Optical.Draws;$uploads=$initial.evidence.gallery.Optical.Uploads
 $idleStart=$clock.Elapsed.TotalSeconds;WaitTo ($idleStart+150)
 $end=Cmd probe
 if($end.evidence.gallery.Optical.Draws -ne $draws -or $end.evidence.gallery.Optical.Uploads -ne $uploads){throw 'Idle submitted draws or textures'}
 $stage='off-idle';CheckOff (Cmd glass off)
 WaitTo ([Math]::Max($DurationSeconds,$clock.Elapsed.TotalSeconds+60))
 $final=Cmd probe;CheckOff $final
 if($final.evidence.gallery.Optical.SnapshotPending -or $final.evidence.gallery.PendingGalleryCovers -ne 0 -or $final.evidence.pendingNavigation -or $final.evidence.pendingDetails -or $final.evidence.staleCovers -ne 0){throw 'Final work not drained'}
 & "$root/command.ps1" -Action exit|Out-Null
 if(!$owned.WaitForExit(15000) -or $owned.ExitCode -ne 0){throw 'Normal exit failed'}
 $p=Get-Content "$root/process.json" -Raw|ConvertFrom-Json
 $after=(Get-FileHash (Join-Path $p.profile 'library.json')).Hash
 if($p.libraryBefore -ne $after){throw 'Test library changed'}
 @{status='PASS';seconds=$clock.Elapsed.TotalSeconds;actions=$actions;cycles=18;exitCode=$owned.ExitCode;libraryBefore=$p.libraryBefore;libraryAfter=$after;advancedIdleSeconds=150;idleDraws=0;idleUploads=0;final=$final.evidence;profile=$p.profile;candidate=$p.candidate;forcedGc=$false;productionAccess=$false;captureDuringRun=$false}|ConvertTo-Json -Depth 22|Set-Content "$root/lifecycle-completion.json"
}catch{
 @{status='FAIL';seconds=$clock.Elapsed.TotalSeconds;error=$_.Exception.ToString()}|ConvertTo-Json|Set-Content "$root/lifecycle-failure.json";throw
}finally{
 if(!$owned.HasExited){try{& "$root/command.ps1" -Action exit|Out-Null}catch{}}
 $stream.Dispose();$owned.Dispose()
}
