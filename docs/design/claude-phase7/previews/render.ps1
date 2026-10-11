# Renders concept/prototype HTML to PNG with headless Edge and a throwaway profile.
# Output is concept-mockup evidence only; it is never WinUI runtime evidence.
param([Parameter(Mandatory)][string[]]$Jobs, [string]$Out = (Join-Path $PSScriptRoot '../../../../.artifacts/claude-design/renders'))
$ErrorActionPreference = 'Stop'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$out = [IO.Path]::GetFullPath($Out); [IO.Directory]::CreateDirectory($out) | Out-Null
$profile = Join-Path ([IO.Path]::GetTempPath()) ('order-preview-' + [guid]::NewGuid().ToString('N'))
try {
  foreach ($job in $Jobs) {
    # job format: name|page?query|width|height
    $name, $page, $w, $h = $job -split '\|'
    $file, $query = $page -split '\?', 2
    $uri = ([Uri](Join-Path $PSScriptRoot $file)).AbsoluteUri + $(if ($query) { "?$query" } else { '' })
    $png = Join-Path $out "$name.png"
    if (Test-Path $png) { Remove-Item $png }
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --user-data-dir="$profile" `
      --window-size="$w,$h" --virtual-time-budget=2500 --screenshot="$png" $uri 2>$null | Out-Null
    if (!(Test-Path $png)) { throw "Render failed: $name" }
    Write-Output $png
  }
} finally { Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue }
