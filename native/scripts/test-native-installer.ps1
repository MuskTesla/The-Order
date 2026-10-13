param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith((Join-Path $repo '.artifacts').TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'A fresh task artifact directory is required.' }
$setupPath = [IO.Path]::GetFullPath($Setup)
$buildPath = [IO.Path]::GetFullPath($BuildDirectory)
$registryKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F7940B13-6814-42F7-AB71-69A9901D7DB3}_is1'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'The Order.lnk'
$group = 'The Order'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) $group
if (Test-Path -LiteralPath $registryKey) { throw 'An existing user installation must not be replaced by this test.' }
if (Test-Path -LiteralPath $desktopLink) { throw 'An existing desktop shortcut must not be replaced by this test.' }
if (Test-Path -LiteralPath $menu) { throw 'An existing Start menu group must not be replaced by this test.' }
if (@(Get-Process -Name 'TheOrder.WinUI' -ErrorAction SilentlyContinue).Count) { throw 'Existing The Order instances must be exited before installer validation.' }
$null = New-Item -ItemType Directory -Path $output
$install = Join-Path $output '安装程序 & app'
$profile = Join-Path $output '测试资料 & profile'
$results = [Collections.Generic.List[object]]::new()
$manifest = Get-Content -LiteralPath (Join-Path $buildPath 'payload/files.json') -Raw | ConvertFrom-Json
$formalRoots = @((Join-Path $env:LOCALAPPDATA 'TheOrder/Profiles/Production'),
    (Join-Path $env:LOCALAPPDATA 'TheOrder/Profiles/Development'), (Join-Path $env:APPDATA 'KunGalManager'))
$formalExistence = @($formalRoots | ForEach-Object { Test-Path -LiteralPath $_ })
function Check([string]$Name, [bool]$Passed, $Details = $null) {
    $results.Add([pscustomobject]@{name=$Name;passed=$Passed;details=$Details})
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'checks.json') -Encoding utf8
    if (!$Passed) { throw "Installer check failed: $Name" }
}
function Run([string]$File, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(90000)) { throw 'Test operation timed out; no process was killed.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Install([string]$Name, [string]$Target = $install) {
    Run $setupPath @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        ('/DIR=' + $Target), ('/GROUP=' + $group), '/TASKS=desktopicon', ('/LOG=' + (Join-Path $output ($Name + '.log'))))
}
function PayloadMatches {
    foreach ($file in $manifest.files) {
        $path = Join-Path $install $file.path
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { return $false }
    }
    return (Get-FileHash -LiteralPath (Join-Path $install 'files.json')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $buildPath 'payload/files.json')).Hash
}
Check 'setup-metadata-version' ((Get-Item -LiteralPath $setupPath).VersionInfo.ProductVersion.Trim() -eq '1.0.0')
Check 'install-current-user-no-restart' ((Install 'install') -eq 0)
Check 'published-655-files-unchanged-after-install' (PayloadMatches)
Check 'native-gui-entry-installed' (Test-Path -LiteralPath (Join-Path $install 'TheOrder.exe'))
$registration = Get-ItemProperty -LiteralPath $registryKey
Check 'Windows-installed-apps-registration' ($registration.DisplayName -eq 'The Order' -and
    $registration.DisplayVersion -eq '1.0.0' -and $registration.InstallLocation.TrimEnd('\') -eq $install) @{
        name=$registration.DisplayName;version=$registration.DisplayVersion;location=$registration.InstallLocation
    }
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($link in @($desktopLink, (Join-Path $menu 'The Order.lnk'))) {
    Check ('shortcut-exists-' + (Split-Path $link -Parent)) (Test-Path -LiteralPath $link)
    $shortcut = $shortcutShell.CreateShortcut($link)
    Check ('shortcut-direct-GUI-entry-' + (Split-Path $link -Parent)) ($shortcut.TargetPath -eq
        (Join-Path $install 'TheOrder.exe') -and $shortcut.WorkingDirectory -eq $install)
}
[Runtime.InteropServices.Marshal]::ReleaseComObject($shortcutShell) | Out-Null
Check 'GUI-entry-validates-without-opening-Production' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only')) -eq 0)
Check 'GUI-entry-rejects-unknown-arguments' ((Run (Join-Path $install 'TheOrder.exe') @('--unsafe-unknown-option')) -ne 0)
Check 'GUI-entry-Test-path-with-spaces-and-metacharacters' ((Run (Join-Path $install 'TheOrder.exe') @(
    '--test-profile', $profile, '--validate-only')) -eq 0)
Check 'validation-does-not-create-Test-profile' (!(Test-Path -LiteralPath $profile))
Check 'GUI-entry-refuses-empty-Test-path' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only', '--test-profile', '')) -ne 0)
Check 'GUI-entry-refuses-missing-Test-path' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only', '--test-profile')) -ne 0)
Check 'GUI-entry-refuses-duplicate-Test-option' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only', '--test-profile', $profile, '--test-profile', $profile)) -ne 0)
Check 'GUI-entry-refuses-relative-Test-path' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only', '--test-profile', 'relative-profile')) -ne 0)
foreach ($formal in $formalRoots) {
    Check ('GUI-entry-refuses-Test-formal-' + (Split-Path $formal -Leaf)) ((Run (Join-Path $install 'TheOrder.exe') @(
        '--test-profile', $formal, '--validate-only')) -ne 0)
}
$foreign = Join-Path $output 'existing-unmanaged-folder'
$null = New-Item -ItemType Directory -Path $foreign
$foreignFile = Join-Path $foreign 'existing-user-file.txt'
[IO.File]::WriteAllText($foreignFile, 'Synthetic file; installer must not overwrite or remove it.')
$foreignHash = (Get-FileHash -LiteralPath $foreignFile).Hash
Check 'installer-refuses-existing-unmanaged-folder' ((Install 'unmanaged-blocked' $foreign) -ne 0)
Check 'unmanaged-folder-kept-unchanged' ((Get-FileHash -LiteralPath $foreignFile).Hash -eq $foreignHash -and
    @(Get-ChildItem -LiteralPath $foreign).Count -eq 1)
foreach ($formal in $formalRoots) {
    Check ('installer-refuses-formal-target-' + (Split-Path $formal -Leaf)) ((Install ('formal-blocked-' + (Split-Path $formal -Leaf)) $formal) -ne 0)
}
$linkParent = Join-Path $output 'linked-directory'
$null = New-Item -ItemType Junction -Path $linkParent -Target $foreign
Check 'installer-refuses-linked-directory' ((Install 'linked-blocked' (Join-Path $linkParent 'app')) -ne 0)
Check 'linked-directory-target-not-written' (!(Test-Path -LiteralPath (Join-Path $foreign 'app')))

# Let the unchanged application bootstrap an empty Test directory. An arbitrary
# pre-existing file correctly causes it to refuse adopting an unmanaged profile.
Check 'GUI-entry-starts-isolated-Test' ((Run (Join-Path $install 'TheOrder.exe') @('--test-profile', $profile)) -eq 0)
$deadline = [DateTime]::UtcNow.AddSeconds(25)
$runtime = $null
do {
    Start-Sleep -Milliseconds 250
    $runtime = Get-Process -Name TheOrder.WinUI -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $install 'TheOrder.WinUI.exe') } | Select-Object -First 1
    if ($runtime) { $runtime.Refresh() }
} while ([DateTime]::UtcNow -lt $deadline -and (!$runtime -or $runtime.MainWindowHandle -eq 0 -or
    !(Test-Path -LiteralPath (Join-Path $profile 'native-profile.json'))))
Check 'installed-application-window-and-ready-profile-created' ($null -ne $runtime -and $runtime.MainWindowHandle -ne 0 -and
    (Test-Path -LiteralPath (Join-Path $profile 'native-profile.json')))
$nativeManifest = Get-Content -LiteralPath (Join-Path $profile 'native-profile.json') -Raw | ConvertFrom-Json
Check 'installed-runtime-uses-Test-profile' ($nativeManifest.ProfileKind -eq 'Test' -and $nativeManifest.State -eq 'Ready') @{
    profile=$profile;pid=$runtime.Id;kind=$nativeManifest.ProfileKind
}
Check 'repair-refuses-running-application' ((Install 'running-repair-blocked') -ne 0)
$uninstaller = Join-Path $install 'unins000.exe'
Check 'uninstall-refuses-running-application' ((Run $uninstaller @(
    '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $output 'running-uninstall-blocked.log')))) -ne 0)
Check 'blocked-operations-kept-payload-unchanged' (PayloadMatches)
Check 'blocked-operations-kept-app-running' (!$runtime.HasExited)

# End only the process this test created, after checking its executable and new
# Test identity again. This is a start-only smoke, not a graceful-exit test.
$runtime.Refresh()
if ($runtime.HasExited -or $runtime.Path -ne (Join-Path $install 'TheOrder.WinUI.exe') -or
    $nativeManifest.ProfileKind -ne 'Test' -or !$profile.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The owned empty Test process no longer matches; it was not terminated.'
}
Stop-Process -Id $runtime.Id
Check 'owned-new-empty-Test-process-ended' ($runtime.WaitForExit(15000))
$runtime.Dispose()
$personal = Join-Path $profile 'personal-edits-sentinel.txt'
[IO.File]::WriteAllText($personal, 'Synthetic personal name / notes / time; outside installed program.')
$personalHash = (Get-FileHash -LiteralPath $personal).Hash
$unknown = Join-Path $install 'unknown-user-file.txt'
[IO.File]::WriteAllText($unknown, 'Unknown local file must survive repair and uninstall.')
$unknownHash = (Get-FileHash -LiteralPath $unknown).Hash
$profileHashes = @(Get-ChildItem -LiteralPath $profile -File | ForEach-Object {
    [pscustomobject]@{path=$_.FullName;hash=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
function ProfileMatches {
    foreach ($file in $profileHashes) {
        if (!(Test-Path -LiteralPath $file.path) -or (Get-FileHash -LiteralPath $file.path).Hash -ne $file.hash) { return $false }
    }
    return $true
}
$damaged = Join-Path $install 'TheOrder.Application.dll'
[IO.File]::WriteAllBytes($damaged, [byte[]](1,2,3,4))
Check 'GUI-entry-refuses-damaged-payload' ((Run (Join-Path $install 'TheOrder.exe') @('--validate-only')) -ne 0)
Check 'same-version-repair-install' ((Install 'repair') -eq 0)
Check 'repair-restores-exact-payload' (PayloadMatches)
Check 'repair-keeps-complete-synthetic-Test-profile' (ProfileMatches)
Check 'repair-keeps-synthetic-personal-edit' ((Get-FileHash -LiteralPath $personal).Hash -eq $personalHash)
Check 'repair-keeps-unmanaged-file' ((Get-FileHash -LiteralPath $unknown).Hash -eq $unknownHash)
Check 'uninstall-completes-no-restart' ((Run $uninstaller @(
    '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $output 'uninstall.log')))) -eq 0)
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ((Test-Path -LiteralPath $uninstaller) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
Check 'uninstall-removes-program-and-uninstall-registration' (!(Test-Path -LiteralPath $registryKey) -and
    !(Test-Path -LiteralPath (Join-Path $install 'TheOrder.WinUI.exe')) -and !(Test-Path -LiteralPath (Join-Path $install 'TheOrder.exe')))
Check 'uninstall-removes-owned-shortcuts' (!(Test-Path -LiteralPath $desktopLink) -and !(Test-Path -LiteralPath $menu))
Check 'uninstall-keeps-synthetic-personal-edit' ((Get-FileHash -LiteralPath $personal).Hash -eq $personalHash)
Check 'uninstall-keeps-unmanaged-file' ((Get-FileHash -LiteralPath $unknown).Hash -eq $unknownHash)
Check 'uninstall-keeps-Test-identity' (Test-Path -LiteralPath (Join-Path $profile 'native-profile.json'))
Check 'uninstall-keeps-complete-synthetic-Test-profile' (ProfileMatches)
Check 'no-formal-profile-created-or-removed' ((Compare-Object $formalExistence @($formalRoots | ForEach-Object { Test-Path -LiteralPath $_ })).Count -eq 0)
$summary = [ordered]@{status='PASS';checks=$results.Count;failed=@($results | Where-Object { !$_.passed }).Count;
    installerSha256=(Get-FileHash -LiteralPath $setupPath).Hash;TestProfile=$profile;installedPayloadFiles=655;
    currentUserInstall=$true;ProductionOpened=$false;realGameLaunched=$false;cleanWindowsVerified=$false;
    userVisualConfirmation='Pending';installDirectory=$install;temporaryRegistrationRemoved=$true;
    runtimeSmoke='GUI entry and Test startup; owned empty process terminated afterwards, graceful exit not retested'}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
