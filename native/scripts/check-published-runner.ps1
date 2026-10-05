param([switch]$WithoutSdkLookup, [Parameter(Mandatory)][string]$PublishedPath, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$taskRunner = Join-Path $PSScriptRoot 'native.ps1'
$taskPreviousPath = $env:PATH
try {
  if ($WithoutSdkLookup) {
    # Leave installed tools untouched. Any SDK probe fails, including the local portable SDK.
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    function Get-Command {
      [CmdletBinding()] param([string]$Name)
      if ($Name -eq 'dotnet') { throw 'Run unexpectedly tried to discover the SDK.' }
      Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
    }
    function Test-Path {
      [CmdletBinding()] param([string]$LiteralPath)
      if ($LiteralPath -like '*tools*dotnet*') { throw 'Run unexpectedly probed the portable SDK.' }
      Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
    }
  }
  $taskOutput = & $taskRunner -Action Run -Published -PublishedPath $PublishedPath -DataDirectory $DataDirectory
  $taskMatch = [regex]::Match(($taskOutput -join "`n"), 'Candidate PID=(\d+);')
  if (-not $taskMatch.Success) { throw 'Runner did not return the launched candidate PID.' }
  $taskProcess = Get-Process -Id ([int]$taskMatch.Groups[1].Value)
  $taskDeadline = [DateTime]::UtcNow.AddSeconds(15)
  do {
    $taskProcess.Refresh()
    if ($taskProcess.HasExited) { throw 'Published candidate exited before opening its window.' }
    if ($taskProcess.MainWindowHandle -ne 0) { break }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $taskDeadline)
  if ($taskProcess.MainWindowHandle -eq 0) { throw 'Published candidate did not open a window.' }
  [pscustomobject]@{ pid=$taskProcess.Id; sdkLookupForbidden=[bool]$WithoutSdkLookup; windowTitle=$taskProcess.MainWindowTitle; path=$taskProcess.Path; runnerOutput=$taskOutput } | ConvertTo-Json
  # Leave this explicitly requested test window open for native UI inspection and normal close.
} finally { $env:PATH = $taskPreviousPath }
