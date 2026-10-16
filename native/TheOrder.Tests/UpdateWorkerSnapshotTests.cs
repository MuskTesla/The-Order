using System.Diagnostics;
using System.Text.Json;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class UpdateWorkerSnapshotTests
{
    [Theory] [InlineData("ready")] [InlineData("locked")] [InlineData("junction")]
    public async Task BackupCopiesUserCompanionsAndNeverPublishesIncompleteSnapshot(string scenario)
    {
        using var fixture = new ProfileFixture();
        using (var profile = fixture.Open()) { }
        var script = Path.Combine(fixture.Parent, "snapshot-test.ps1");
        // Import only reviewed functions via the PowerShell parser. Never run the
        // worker's installation body, even for a synthetic native profile.
        File.WriteAllText(script, """
param([string]$Source,[string]$Root,[string]$Scenario)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($Source,[ref]$tokens,[ref]$errors)
if($errors.Count -ne 0){throw 'WorkerParseFailure'}
$functions=@('PlainPath','Same','WriteJson','ReleaseVersion','CanApply','SnapshotProfile')
foreach($name in $functions){
 $node=$ast.Find({param($a) $a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true)
 if(!$node){throw 'MissingReviewedFunction'}
 . ([scriptblock]::Create($node.Extent.Text))
}
$job=@{profile=$Root;id=([guid]::NewGuid().ToString('N'));profileId='synthetic';storeId='synthetic';physicalRootHash='synthetic';currentVersion='1.1.1';version='1.2.0-beta.2'}
$null=[IO.Directory]::CreateDirectory((Join-Path $Root 'native-media-identities-v1'))
[IO.File]::WriteAllText((Join-Path $Root 'native-media-identities-v1\decisions.json'),'synthetic user decisions')
[IO.File]::WriteAllText((Join-Path $Root 'native-content-language-v1.json'),'synthetic language choice')
[IO.File]::WriteAllText((Join-Path $Root 'native-global-launch-v1.json'),'synthetic launch policy')
[IO.File]::WriteAllText((Join-Path $Root 'native-book-publications-v1.json'),'synthetic ISBN choices')
[IO.File]::WriteAllBytes((Join-Path $Root 'synthetic-credential.dpapi'),[byte[]]@(1,2,3,4))
$null=[IO.Directory]::CreateDirectory((Join-Path $Root 'native-anilist-v1'))
[IO.File]::WriteAllText((Join-Path $Root 'native-anilist-v1\remote.json'),'discardable remote data')
$held=$null
if($Scenario -eq 'locked'){$held=[IO.File]::Open((Join-Path $Root 'library.json'),'Open','ReadWrite','None')}
if($Scenario -eq 'junction'){
 $outside=Join-Path ([IO.Path]::GetDirectoryName($Root)) 'outside';$null=[IO.Directory]::CreateDirectory($outside)
 $null=New-Item -ItemType Junction -Path (Join-Path $Root 'escape') -Target $outside
}
$success=$false;$snapshot=$null
try{$snapshot=SnapshotProfile $job;$success=$true}catch{if($Scenario -eq 'ready'){throw}}finally{if($held){$held.Dispose()}}
$path=Join-Path (Join-Path (Join-Path $Root 'channel-backups') $job.id) 'snapshot.json'
if($Scenario -eq 'ready'){
 if(!$success){throw 'MissingBackup'}
 $value=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 if($value.state -ne 'Ready'){throw 'NotCommitted'}
 foreach($file in $value.files){
   if((Get-FileHash -LiteralPath (Join-Path $Root $file.path)).Hash -ne $file.sha256){throw 'SourceChanged'}
   if((Get-FileHash -LiteralPath (Join-Path (Join-Path $snapshot 'data') $file.path)).Hash -ne $file.sha256){throw 'CopyChanged'}
 }
 foreach($name in @('library.json','native-profile.json','native-bootstrap.json','native-online-key.dpapi','synthetic-credential.dpapi','native-media-identities-v1\decisions.json','native-global-launch-v1.json','native-book-publications-v1.json','native-content-language-v1.json')){
  if($name -notin @($value.files|ForEach-Object{$_.path})){throw 'MissingUserData'}
 }
 if(@($value.files|Where-Object {$_.path -like 'native-anilist-v1*' -or $_.path -like 'updates*' -or $_.path -like 'channel-backups*' -or $_.path -eq '.the-order-writer.lock'}).Count -ne 0){throw 'IncludedRemoteCache'}
}else{if($success -or (Test-Path -LiteralPath $path)){throw 'IncompleteSnapshotPublished'}}
if(!(CanApply (ReleaseVersion '1.2.0-beta.2') (ReleaseVersion '1.1.1') 1)){throw 'RollbackRejected'}
if(CanApply (ReleaseVersion '1.2.0-beta.2') (ReleaseVersion '1.1.1') 0){throw 'UnverifiedRollback'}
if(CanApply (ReleaseVersion '1.2.0-beta.10') (ReleaseVersion '1.2.0-beta.9') 1){throw 'BetaDowngrade'}
if(CanApply (ReleaseVersion '1.1.1') (ReleaseVersion '1.2.0-beta.2') 0){throw 'UnverifiedBeta'}
[pscustomobject]@{passed=$true;scenario=$Scenario;installationStarted=$false;formalProfileOpened=$false}|ConvertTo-Json
""");
        var start = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach(var a in new[]{"-NoLogo","-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-Source",Path.Combine(ReferenceSources.Repository(),"native/scripts/update-worker.ps1"),"-Root",fixture.Root,"-Scenario",scenario}) start.ArgumentList.Add(a);
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}catch{process.Kill(true);throw;}
        Assert.True(process.ExitCode==0, await output + await error);
        using var result=JsonDocument.Parse(await output);Assert.True(result.RootElement.GetProperty("passed").GetBoolean());
        if(scenario=="junction")Directory.Delete(Path.Combine(fixture.Root,"escape"));
    }
}
