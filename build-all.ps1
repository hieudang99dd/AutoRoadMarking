[CmdletBinding()]
param(
    [ValidateSet('2024', '2025', '2026')]
    [string[]]$Versions = @('2024', '2025', '2026'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$singleBuild = Join-Path $root 'build-release.ps1'
$passed = @()
$failed = @()

foreach ($version in $Versions) {
    Write-Host ""
    Write-Host "===== Civil 3D $version / $Configuration =====" -ForegroundColor Cyan
    try {
        & $singleBuild -Civil3DVersion $version -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Child build failed with exit code $LASTEXITCODE"
        }
        $passed += $version
    }
    catch {
        $failed += "$version : $($_.Exception.Message)"
        Write-Warning "Civil 3D $version build failed: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "===== BUILD SUMMARY =====" -ForegroundColor Cyan
Write-Host "PASS: $($passed -join ', ')"
if ($failed.Count -gt 0) {
    foreach ($failure in $failed) { Write-Host "FAIL: $failure" -ForegroundColor Red }
    throw "Build All finished with $($failed.Count) failed version(s)."
}
Write-Host 'All requested Civil 3D versions built successfully.' -ForegroundColor Green
