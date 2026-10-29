# Compatibility alias. The suite asserts actual foreground HWND, iconic state and full logs.
# Exit 2 means SKIP, not PASS. No running guard is stopped.
param([switch]$Interactive)
& (Join-Path $PSScriptRoot 'test-desktop.ps1') -Interactive:$Interactive
exit $LASTEXITCODE
