param([string]$Candidate,[string]$DotnetPath,[switch]$Wait,[string]$Run)
# Separate opt-in entry. No frozen historic launcher/configuration is changed.
$arguments=@{Diagnostics=$true;Wait=[bool]$Wait}
foreach($name in @('Candidate','DotnetPath','Run')){if($PSBoundParameters.ContainsKey($name)){$arguments[$name]=$PSBoundParameters[$name]}}
& (Join-Path $PSScriptRoot 'run-integrated-ui.ps1') @arguments
