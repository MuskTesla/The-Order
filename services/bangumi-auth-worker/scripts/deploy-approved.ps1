param([switch]$ValidateOnly, [switch]$UserApprovedProductionDeployment, [switch]$UserConfirmedSecretRotation)
$ErrorActionPreference='Stop'
if (!$ValidateOnly -and (!$UserApprovedProductionDeployment -or !$UserConfirmedSecretRotation)) {
    throw 'Requires direct user confirmation of this reviewed production deployment and old-secret revocation. Flags never replace user authorization.'
}
Push-Location ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $reviewRoot = Join-Path $repo '.artifacts/bangumi-worker-20261014'
    $review = Get-Content -LiteralPath (Join-Path $reviewRoot 'predeployment-review.json') -Raw | ConvertFrom-Json
    if ($review.localImplementationGate -ne 'PASS' -or !$review.secretRotationConfirmedByUser -or
        (git -C $repo rev-parse HEAD) -ne $review.sourceHead) { throw 'Reviewed source/rotation receipt required.' }
    foreach ($file in (Get-Content -LiteralPath (Join-Path $reviewRoot 'worker-reviewed-manifest.json') -Raw | ConvertFrom-Json)) {
        $path = [IO.Path]::GetFullPath((Join-Path $repo $file.path))
        if (!$path.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw 'Source changed after review; revalidate before deployment.' }
    }
    & node 'scripts/check-deployment.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Deployment configuration verification failed.' }
    if ($ValidateOnly) {
        Write-Output 'Reviewed source and deployment configuration verified; no deployment was performed.'
        return
    }
    $env:WRANGLER_SEND_METRICS='false'
    # No --env: dashboard Production is this existing Worker's default deployment.
    # keep_vars preserves the console Client ID. Secrets are never read/written/deleted.
    & 'node_modules/.bin/wrangler.cmd' deploy --keep-vars
    if ($LASTEXITCODE -ne 0) { throw 'Deployment did not complete; do not enable authorization.' }
    # The checked first deployment intentionally keeps BANGUMI_AUTH_ENABLED=false.
    # Enabling it and real consent require the separately requested approval.
} finally { Pop-Location }
