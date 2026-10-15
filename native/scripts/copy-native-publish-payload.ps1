# Packaging helper shared by the RC packager and its synthetic filesystem test.
# Symbols remain in the build output for diagnostics; they are not runtime files
# and may contain absolute source paths, so they do not belong in distribution.
function Copy-NativePublishPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishedDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    $source = [IO.Path]::GetFullPath($PublishedDirectory).TrimEnd('\')
    $output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    if (!(Test-Path -LiteralPath $source -PathType Container) -or
        !(Test-Path -LiteralPath $output -PathType Container)) {
        throw 'Existing publish and fresh package directories are required.'
    }
    if ($source.Equals($output, [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish and package directories must not overlap.'
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $source -Recurse -File -Force | Sort-Object FullName)) {
        if ($file.Extension.Equals('.pdb', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $destination = Join-Path $output $relative
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
