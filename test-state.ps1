# Deterministic rule and event-sequence regressions; no real input, lock or focus changes.
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts'
& (Join-Path $PSScriptRoot 'build.ps1') -SkipStop -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $output 'LogicTest.exe')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $output 'StateTest.exe')
exit $LASTEXITCODE
