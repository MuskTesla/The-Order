param([string]$Configuration = 'Debug', [string]$Output = '.artifacts/native-phase5a/synthetic')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$destination = [IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts')) + '\'
if (-not $destination.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $destination)) { throw 'Expected a new directory inside repository artifacts.' }
$fixtureSource = Join-Path $repoRoot "native/TheOrder.LaunchFixture/bin/$Configuration/net10.0-windows10.0.26100.0"
if (-not (Test-Path -LiteralPath "$fixtureSource/TheOrder.LaunchFixture.exe")) { throw 'Build fixture first.' }
$profilePath = Join-Path $destination 'profile'
$reportPath = Join-Path $destination 'observations'
New-Item -ItemType Directory -Path $profilePath,$reportPath -Force | Out-Null
Set-Content -LiteralPath "$profilePath/.the-order-synthetic" -Value 'the-order-native-phase1-synthetic-v1' -NoNewline
Set-Content -LiteralPath "$reportPath/.the-order-launch-fixture" -Value 'owned synthetic fixture' -NoNewline
$games = @()
foreach ($mode in @('game','launcher','chain','empty','multiple','missing')) {
    $gamePath = Join-Path $destination "games/$mode"
    New-Item -ItemType Directory -Path $gamePath -Force | Out-Null
    Get-ChildItem -LiteralPath $fixtureSource -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $gamePath }
    foreach ($exeName in @('simple_game','child_game','second_game','helper')) { Copy-Item -LiteralPath "$fixtureSource/TheOrder.LaunchFixture.exe" -Destination "$gamePath/$exeName.exe" }
    $executable = if ($mode -eq 'missing') { "$gamePath/absent.exe" } else { "$gamePath/simple_game.exe" }
    $games += @{
        id = $mode; name = "合成启动 · $mode"; nameIsCustom = $true; metadata = $null
        installations = @(@{ id = "install-$mode"; folderPath = $gamePath; executablePath = [IO.Path]::GetFullPath($executable); arguments = @($reportPath,$mode,$mode) })
        preferredInstallationId = "install-$mode"; status = 'planned'; personalRating = $null; favorite = $false; categories = @(); customTags = @(); notes = 'Phase 5A synthetic only'
        totalPlaySeconds = 720; playCount = 3; sessions = @(); addedAt = '2026-10-05T00:00:00.000Z'; lastPlayedAt = $null
    }
}
@{ format = 'kun-gal-manager'; version = 1; exportedAt = '2026-10-05T00:00:00.000Z'; games = $games } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$profilePath/library.json" -Encoding utf8
Write-Output $profilePath
