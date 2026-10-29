# Covered by deterministic event-sequence tests. Never locks or changes the user's desktop.
& (Join-Path $PSScriptRoot 'test-state.ps1')
exit $LASTEXITCODE
