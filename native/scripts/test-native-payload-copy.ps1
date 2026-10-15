param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = (Join-Path $repo '.artifacts').TrimEnd('\') + '\'
if (!$output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Fresh workspace fixture output required.' }
$null = [IO.Directory]::CreateDirectory($output)
$source = Join-Path $output 'publish fixture'
$target = Join-Path $output 'package fixture'
foreach ($path in @($source, $target, (Join-Path $source 'zh-Hans/Views'), (Join-Path $source 'Assets'))) {
    $null = [IO.Directory]::CreateDirectory($path)
}
$files = @('TheOrder.WinUI.exe', 'TheOrder.WinUI.dll', 'TheOrder.WinUI.pri', 'TheOrder.WinUI.runtimeconfig.json',
    'zh-Hans/Views/Details.xbf', 'Assets/Brand.png', 'TheOrder.WinUI.pdb', 'zh-Hans/Views/satellite.PDB')
foreach ($file in $files) { [IO.File]::WriteAllText((Join-Path $source $file), 'synthetic publish fixture: ' + $file) }
$sourceHashes = @{}
foreach ($file in $files) { $sourceHashes[$file] = (Get-FileHash -LiteralPath (Join-Path $source $file)).Hash }
. (Join-Path $PSScriptRoot 'copy-native-publish-payload.ps1')
Copy-NativePublishPayload -PublishedDirectory $source -OutputDirectory $target
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    $checks.Add([ordered]@{name=$Name;passed=$Passed})
    if (!$Passed) { throw ('Payload copy failed: ' + $Name) }
}
Check 'root-and-nested-symbols-excluded-case-insensitively' (@(Get-ChildItem -LiteralPath $target -Recurse -File -Filter '*.pdb').Count -eq 0)
Check 'runtime-and-resource-file-count-retained' (@(Get-ChildItem -LiteralPath $target -Recurse -File).Count -eq 6)
foreach ($file in $files | Where-Object { [IO.Path]::GetExtension($_) -ine '.pdb' }) {
    $destination = Join-Path $target $file
    Check ('runtime-bytes-preserved-' + $file) ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $destination).Hash -eq $sourceHashes[$file])
}
Check 'original-publish-and-diagnostic-symbols-unchanged' (@($files | Where-Object {
    !(Test-Path -LiteralPath (Join-Path $source $_) -PathType Leaf) -or
    (Get-FileHash -LiteralPath (Join-Path $source $_)).Hash -ne $sourceHashes[$_]
}).Count -eq 0)
$rejected = $false
try { Copy-NativePublishPayload -PublishedDirectory $source -OutputDirectory $source } catch { $rejected = $true }
Check 'overlapping-source-and-destination-refused' $rejected
[ordered]@{passed=@($checks | Where-Object passed).Count;failed=@($checks | Where-Object { !$_.passed }).Count;
    results=$checks;fixtureOnly=$true;applicationStarted=$false;installerExecuted=$false;productionAccessed=$false;
    helperSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'copy-native-publish-payload.ps1')).Hash} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw
