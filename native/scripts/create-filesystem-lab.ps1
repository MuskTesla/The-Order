param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $labRoot)) { throw 'A new absolute lab directory is required.' }
New-Item -ItemType Directory -Path $labRoot | Out-Null
$gameRoot = Join-Path $labRoot 'Game with a long name'
New-Item -ItemType Directory -Path $gameRoot | Out-Null
Set-Content -LiteralPath (Join-Path $gameRoot 'game.exe') -Value 'Synthetic text only. Not an executable.'
$cases = [Collections.Generic.List[object]]::new()
function Add-LabCase($Name, $Status, $Path, $Reason) { $cases.Add([ordered]@{ name=$Name; fixture=$Status; path=$Path; reason=$Reason }) }
Add-LabCase 'normal' 'created' $gameRoot ''
Add-LabCase 'trailing-separator' 'created' ($gameRoot + '\') ''
Add-LabCase 'dot' 'created' ($gameRoot + '\.') ''
Add-LabCase 'dotdot' 'created' ($gameRoot + '\..\Game with a long name') ''
Add-LabCase 'case' 'created' $gameRoot.ToUpperInvariant() ''
Add-LabCase 'slash' 'created' $gameRoot.Replace('\','/') ''
$junctionPath = Join-Path $labRoot 'junction'
New-Item -ItemType Junction -Path $junctionPath -Target $gameRoot | Out-Null
Add-LabCase 'junction' 'created' $junctionPath ''
$missingTarget = Join-Path $labRoot 'missing-target'
New-Item -ItemType Directory -Path $missingTarget | Out-Null
$brokenPath = Join-Path $labRoot 'broken-junction'
New-Item -ItemType Junction -Path $brokenPath -Target $missingTarget | Out-Null
Remove-Item -LiteralPath $missingTarget
Add-LabCase 'broken-link' 'created' $brokenPath ''
try {
    $symbolicPath = Join-Path $labRoot 'symlink'
    New-Item -ItemType SymbolicLink -Path $symbolicPath -Target $gameRoot | Out-Null
    Add-LabCase 'symlink' 'created' $symbolicPath ''
} catch { Add-LabCase 'symlink' 'unsupported' $null $_.Exception.Message }
$longPath = Join-Path (Join-Path (Join-Path $labRoot ('a'*100)) ('b'*100)) ('c'*100)
New-Item -ItemType Directory -Path $longPath | Out-Null
Add-LabCase 'long-path' 'created' $longPath ''
$hardLink = Join-Path $labRoot 'hardlinked.exe'
New-Item -ItemType HardLink -Path $hardLink -Target (Join-Path $gameRoot 'game.exe') | Out-Null
Add-LabCase 'file-hardlink' 'created' $hardLink 'Compare to game.exe, not directory identity.'
Add-LabCase 'relative-path' 'unsupported' '.\Game with a long name' 'Application accepts explicit absolute picker paths only; relative path must fail preflight.'
Add-LabCase 'unc' 'skipped' $null 'No synthetic SMB share was supplied. Do not create a share or access administrative shares.'
Add-LabCase 'generic-reparse' 'skipped' $null 'No third-party reparse provider fixture. Adapter rejects tags other than mount point and symlink.'
# 8.3 and subst are observed by the C# capability test, which releases any temporary drive in finally.
Add-LabCase '8.3' 'pending' $gameRoot 'GetShortPathName determines whether this volume supplies an actual alternate name.'
Add-LabCase 'subst' 'pending' $labRoot 'Temporary unused drive only; remove mapping after identity comparison.'
$cases | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $labRoot 'fixtures.json') -Encoding utf8
Write-Output $labRoot
