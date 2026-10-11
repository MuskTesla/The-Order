param([switch]$PassThru)
$ErrorActionPreference = 'Stop'
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCandidate = Join-Path $taskProject '.artifacts\overnight-2026-10-04\candidate\win-unpacked\The Order.exe'
$taskProfile = Join-Path $taskProject '.artifacts\overnight-2026-10-04\trial-profile'
if (-not (Test-Path -LiteralPath $taskCandidate -PathType Leaf)) { throw '夜间候选程序尚未打包，请查看 docs/ai/OVERNIGHT.md。' }
& node (Join-Path $taskProject 'scripts\overnight-trial-data.mjs') | Out-Host
if ($LASTEXITCODE -ne 0) { throw '隔离试用资料准备失败，未启动程序。' }
Write-Host "启动夜间候选程序；只使用隔离数据目录：$taskProfile"
Write-Host "书架与游玩记录均为虚构样例；本地封面测试图：$(Join-Path $taskProfile 'synthetic-photos')"
Write-Host '联网资料服务没有预置密钥，可在候选程序的设置中自行填写。'
$taskPreviousData = $env:KGM_DATA_DIR
$taskPreviousDev = $env:KGM_DEV_URL
try {
  $env:KGM_DATA_DIR = $taskProfile
  Remove-Item Env:KGM_DEV_URL -ErrorAction SilentlyContinue
  # This is the user's interactive candidate window, not a background helper.
  $taskProcess = Start-Process -FilePath $taskCandidate -WorkingDirectory (Split-Path -Parent $taskCandidate) -WindowStyle Normal -PassThru
  if ($PassThru) { $taskProcess }
} finally {
  if ($null -eq $taskPreviousData) { Remove-Item Env:KGM_DATA_DIR -ErrorAction SilentlyContinue } else { $env:KGM_DATA_DIR = $taskPreviousData }
  if ($null -eq $taskPreviousDev) { Remove-Item Env:KGM_DEV_URL -ErrorAction SilentlyContinue } else { $env:KGM_DEV_URL = $taskPreviousDev }
}
