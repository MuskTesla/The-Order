param([ValidateSet('01', '02', '03', '04')][string]$Iteration = '01')
$ErrorActionPreference = 'Stop'
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCandidate = Join-Path $taskProject ".artifacts\iteration-$Iteration\candidate\win-unpacked\The Order.exe"
$taskProfile = Join-Path $taskProject ".artifacts\iteration-$Iteration\trial-profile"
if (-not (Test-Path -LiteralPath $taskCandidate -PathType Leaf)) { throw '候选程序尚未打包，请查看 docs/ai/ITERATION.md。' }
& node (Join-Path $taskProject 'tests\iteration-trial-data.mjs') --iteration $Iteration
if ($LASTEXITCODE -ne 0) { throw '隔离试用书架准备失败，未启动程序。' }
Write-Host "启动候选程序；只使用隔离数据目录：$taskProfile"
$taskPreviousData = $env:KGM_DATA_DIR
$taskPreviousDev = $env:KGM_DEV_URL
try {
  $env:KGM_DATA_DIR = $taskProfile
  Remove-Item Env:KGM_DEV_URL -ErrorAction SilentlyContinue
  # Candidate trial is an interactive window the user needs to see.
  Start-Process -FilePath $taskCandidate -WorkingDirectory (Split-Path -Parent $taskCandidate) -WindowStyle Normal
} finally {
  if ($null -eq $taskPreviousData) { Remove-Item Env:KGM_DATA_DIR -ErrorAction SilentlyContinue } else { $env:KGM_DATA_DIR = $taskPreviousData }
  if ($null -eq $taskPreviousDev) { Remove-Item Env:KGM_DEV_URL -ErrorAction SilentlyContinue } else { $env:KGM_DEV_URL = $taskPreviousDev }
}
