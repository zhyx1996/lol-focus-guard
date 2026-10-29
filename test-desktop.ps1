# Opt-in desktop integration: only creates/cleans its own fixtures and private log.
param([switch]$Interactive)
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts'
& (Join-Path $PSScriptRoot 'build.ps1') -SkipStop -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$arguments = @()
if ($Interactive) { $arguments += '--interactive' }
& (Join-Path $output 'DesktopTest.exe') @arguments
exit $LASTEXITCODE
