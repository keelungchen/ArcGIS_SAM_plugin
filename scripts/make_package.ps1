# ============================================================
#  Build the portable installer package for the ArcGIS SAM plugin.
#
#  Collects everything a target computer needs into one zip:
#    INSTALL.bat, installer\, the built .esriAddinX,
#    python_server\, sam3_tools\, models\, docs\, the toolbox.
#
#  Usage (on the development machine):
#    powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1
#
#  Output: dist_package\ArcGIS_SAM_plugin_Setup.zip
#  On the target computer: extract the zip anywhere and
#  double-click INSTALL.bat.
# ============================================================

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

$addinX = Join-Path $RepoRoot 'csharp_addin\dist\SAM3Interactive.esriAddinX'
if (-not (Test-Path $addinX)) {
    Write-Host "Add-in package missing - building it first ..."
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build_addin.ps1')
    if (-not (Test-Path $addinX)) {
        Write-Host "[FAIL] build_addin.ps1 did not produce $addinX" -ForegroundColor Red
        exit 1
    }
}

$stage = Join-Path $env:TEMP ("sam3_pkg_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "Staging in $stage ..."

function StageTree($name) {
    $src = Join-Path $RepoRoot $name
    if (-not (Test-Path $src)) {
        Write-Host "[FAIL] required folder missing: $src" -ForegroundColor Red
        exit 1
    }
    robocopy $src (Join-Path $stage $name) /E /NJH /NJS /NFL /NDL /XD __pycache__ .git | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Write-Host "[FAIL] copying $name failed (robocopy $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Write-Host "  + $name"
}

StageTree 'python_server'
StageTree 'sam3_tools'
StageTree 'models'
StageTree 'docs'
StageTree 'installer'

New-Item -ItemType Directory -Force (Join-Path $stage 'csharp_addin\dist') | Out-Null
Copy-Item $addinX (Join-Path $stage 'csharp_addin\dist') -Force
Write-Host "  + csharp_addin\dist\SAM3Interactive.esriAddinX"

foreach ($f in @('INSTALL.bat', 'SAM3_Toolbox.pyt', 'README.md')) {
    $src = Join-Path $RepoRoot $f
    if (Test-Path $src) {
        Copy-Item $src $stage -Force
        Write-Host "  + $f"
    }
}
Get-ChildItem (Join-Path $RepoRoot '*.pyt.xml') -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName $stage -Force; Write-Host "  + $($_.Name)" }

$outDir = Join-Path $RepoRoot 'dist_package'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir 'ArcGIS_SAM_plugin_Setup.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Compressing ..."
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item -Recurse -Force $stage

$sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "[ OK ] Package created: $zip ($sizeMB MB)" -ForegroundColor Green
Write-Host "       Copy the zip to the target computer, extract it"
Write-Host "       anywhere, and double-click INSTALL.bat."
