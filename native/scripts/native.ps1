param(
  [ValidateSet('Restore','Build','Test','Publish','Run')][string]$Action = 'Build',
  [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
  [string]$DotnetPath,
  [string]$DataDirectory,
  [switch]$InstallSdk,
  [switch]$Published,
  [string]$PublishedPath
)
$ErrorActionPreference = 'Stop'
$taskNative = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRepository = Split-Path $taskNative -Parent
$taskArtifacts = Join-Path $taskRepository '.artifacts/native-phase1'
$taskSdkDirectory = Join-Path $taskArtifacts 'tools/dotnet'
$taskSdkVersion = '10.0.401'
if ($InstallSdk -and $Action -ne 'Run') {
  # Official archive, exact version and SHA-512 from Microsoft's release manifest.
  # No registry, global PATH, workload, administrator or system installer changes.
  $taskMetadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
  $taskSdk = $taskMetadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $taskSdkVersion | Select-Object -First 1
  $taskFile = $taskSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
  if (-not $taskFile -or -not $taskFile.url.StartsWith('https://builds.dotnet.microsoft.com/dotnet/')) { throw 'Official pinned SDK archive not found.' }
  New-Item -ItemType Directory -Force (Join-Path $taskArtifacts 'tools') | Out-Null
  $taskArchive = Join-Path $taskArtifacts 'tools/dotnet-sdk.zip'
  Invoke-WebRequest $taskFile.url -OutFile $taskArchive
  if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA512).Hash -ne $taskFile.hash) { throw 'SDK checksum mismatch.' }
  Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskSdkDirectory -Force
}
if ($Action -ne 'Run' -and -not $DotnetPath) {
  $taskLocal = Join-Path $taskSdkDirectory 'dotnet.exe'
  if (Test-Path -LiteralPath $taskLocal) { $DotnetPath = $taskLocal }
  else { $DotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
}
if ($Action -ne 'Run' -and -not $DotnetPath) { throw 'Missing .NET 10.0.401 SDK. Install the official SDK, or use -InstallSdk for an isolated archive.' }
$taskPreviousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$taskPreviousData = $env:THE_ORDER_NATIVE_DATA_DIR
function Invoke-NativeDotnet([string[]]$Arguments) {
  & $DotnetPath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)." }
}
Push-Location $taskNative
try {
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  switch ($Action) {
    'Restore' { Invoke-NativeDotnet @('restore','TheOrder.slnx','--locked-mode') }
    'Build' { Invoke-NativeDotnet @('build','TheOrder.slnx','-c',$Configuration,'--no-restore') }
    'Test' { Invoke-NativeDotnet @('test','TheOrder.Tests/TheOrder.Tests.csproj','-c',$Configuration,'--no-restore','--logger','trx;LogFileName=compatibility.trx','--results-directory',(Join-Path $taskArtifacts 'test-results')) }
    'Publish' { Invoke-NativeDotnet @('publish','TheOrder.WinUI/TheOrder.WinUI.csproj','-c','Release','--no-restore','-o',(Join-Path $taskArtifacts 'publish/win-x64')) }
    'Run' {
      if (-not $DataDirectory) { $DataDirectory = Join-Path $taskNative 'fixtures/normal' }
      if (-not [IO.Path]::IsPathFullyQualified($DataDirectory)) { throw 'DataDirectory must be absolute.' }
      $env:THE_ORDER_NATIVE_DATA_DIR = $DataDirectory
      if ($Published -and (-not $PublishedPath -or -not [IO.Path]::IsPathFullyQualified($PublishedPath))) { throw '-Published requires an explicit absolute -PublishedPath to the checkpoint-bound EXE.' }
      if ($PublishedPath -and -not $Published) { throw '-PublishedPath requires -Published.' }
      $taskExe = if ($Published) { $PublishedPath } else { Join-Path $taskNative "TheOrder.WinUI/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/TheOrder.WinUI.exe" }
      if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Build or publish the candidate first.' }
      # This is the explicitly requested interactive candidate window.
      $taskProcess = Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path $taskExe) -WindowStyle Normal -PassThru
      Write-Output "Candidate PID=$($taskProcess.Id); synthetic data=$DataDirectory"
    }
  }
} finally {
  Pop-Location
  if ($null -eq $taskPreviousTelemetry) { Remove-Item Env:DOTNET_CLI_TELEMETRY_OPTOUT -ErrorAction SilentlyContinue } else { $env:DOTNET_CLI_TELEMETRY_OPTOUT = $taskPreviousTelemetry }
  if ($null -eq $taskPreviousData) { Remove-Item Env:THE_ORDER_NATIVE_DATA_DIR -ErrorAction SilentlyContinue } else { $env:THE_ORDER_NATIVE_DATA_DIR = $taskPreviousData }
}
