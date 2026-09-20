[CmdletBinding()]
param(
    [ValidateSet('2024','2025','2026')]
    [string]$Civil3DVersion = '2026',

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [string]$AutoCadRoot = '',
    [string]$Civil3DRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'AutoRoadMarking_Pro.slnx'
$cadHost = Join-Path $root 'Autoroadmarking_Pro.CadHost\Autoroadmarking_Pro.CadHost.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Không tìm thấy dotnet SDK trong PATH.'
}

if (-not $AutoCadRoot) {
    $AutoCadRoot = "C:\Program Files\Autodesk\AutoCAD $Civil3DVersion"
}
if (-not $Civil3DRoot) {
    $Civil3DRoot = Join-Path $AutoCadRoot 'C3D'
}

$required = @(
    (Join-Path $AutoCadRoot 'AcCoreMgd.dll'),
    (Join-Path $AutoCadRoot 'AcDbMgd.dll'),
    (Join-Path $AutoCadRoot 'AcMgd.dll'),
    (Join-Path $Civil3DRoot 'AeccDbMgd.dll')
)
foreach ($file in $required) {
    if (-not (Test-Path $file)) { throw "Thiếu Autodesk reference: $file" }
}

Write-Host "== Static QA ==" -ForegroundColor Cyan
& python (Join-Path $root 'tools\qa-static.py')
if ($LASTEXITCODE -ne 0) { throw 'Static QA failed.' }

Write-Host "== Restore ==" -ForegroundColor Cyan
& dotnet restore $cadHost `
    -p:Civil3DVersion=$Civil3DVersion `
    -p:AutoCadRoot="$AutoCadRoot" `
    -p:Civil3DRoot="$Civil3DRoot"
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host "== Build Civil 3D $Civil3DVersion / $Configuration ==" -ForegroundColor Cyan
& dotnet build $cadHost `
    -c $Configuration `
    --no-restore `
    -p:Civil3DVersion=$Civil3DVersion `
    -p:AutoCadRoot="$AutoCadRoot" `
    -p:Civil3DRoot="$Civil3DRoot"
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

$out = Join-Path $root "Autoroadmarking_Pro.CadHost\bin\$Configuration\C3D$Civil3DVersion"
Write-Host "Build OK: $out" -ForegroundColor Green
