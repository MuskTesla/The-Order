param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Output,
    [string]$Checkpoint = (git rev-parse HEAD)
)
$ErrorActionPreference = 'Stop'
foreach ($taskPath in @($Directory,$Output)) {
    if (-not [IO.Path]::IsPathFullyQualified($taskPath) -or $taskPath -notmatch '[\\/]\.artifacts[\\/]') { throw 'Artifact paths required' }
}
$taskAssemblies = @(foreach ($taskFile in Get-ChildItem -LiteralPath $Directory -Filter 'TheOrder.*.dll' -File) {
    $taskStream = [IO.File]::OpenRead($taskFile.FullName)
    $taskPe = $null
    try {
        $taskPe = [Reflection.PortableExecutable.PEReader]::new($taskStream)
        $taskReader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($taskPe)
        $taskMvid = $taskReader.GetGuid($taskReader.GetModuleDefinition().Mvid).ToString()
    } finally { if ($taskPe) { $taskPe.Dispose() }; $taskStream.Dispose() }
    $taskPdb = [IO.Path]::ChangeExtension($taskFile.FullName,'.pdb')
    $taskSources = @()
    if ([IO.File]::Exists($taskPdb)) {
        $taskStream = [IO.File]::OpenRead($taskPdb); $taskProvider = $null
        try {
            $taskProvider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($taskStream,[Reflection.Metadata.MetadataStreamOptions]::LeaveOpen,0)
            $taskReader = $taskProvider.GetMetadataReader([Reflection.Metadata.MetadataReaderOptions]::Default,$null)
            $taskSources = @(foreach ($taskHandle in $taskReader.Documents) {
                $taskDocument = $taskReader.GetDocument($taskHandle)
                $taskName = $taskReader.GetString($taskDocument.Name)
                if ($taskName -match '[\\/]obj[\\/]') { continue }
                $taskHash = [Convert]::ToHexString($taskReader.GetBlobBytes($taskDocument.Hash))
                $taskActual = (Get-FileHash -LiteralPath $taskName -Algorithm SHA256).Hash
                if ($taskActual -cne $taskHash) { throw "Source checksum mismatch: $taskName" }
                [ordered]@{file=$taskName;sha256=$taskHash;matchesCurrent=$true}
            })
        } finally { if ($taskProvider) { $taskProvider.Dispose() }; $taskStream.Dispose() }
    }
    [ordered]@{file=$taskFile.Name;sha256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash;mvid=$taskMvid;sources=$taskSources}
})
$taskExecutables = @(Get-ChildItem -LiteralPath $Directory -Filter 'TheOrder.*.exe' -File | ForEach-Object { [ordered]@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
[ordered]@{checkpoint=$Checkpoint;nativeTree=(git rev-parse ($Checkpoint+':native'));createdAt=(Get-Date).ToString('o');directory=$Directory;assemblies=$taskAssemblies;executables=$taskExecutables;productionAccess=$false} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Output "Bound $($taskAssemblies.Count) assemblies and $($taskExecutables.Count) executables"
