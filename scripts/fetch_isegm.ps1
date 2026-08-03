# Downloads TagLab's vendored RITM inference code (models/isegm) into
# python_server\isegm and renames the namespace back to plain 'isegm'.
# Called by get_ritm.bat; safe to run standalone.
#
# Note: the original RITM repository (SamsungLabs/saic-vul) is no
# longer available on GitHub, so TagLab's copy - the one its
# ritm_corals.pth checkpoint was built for - is fetched file by file
# via the GitHub API (the full TagLab zip is very large).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "python_server\isegm"

if (Test-Path (Join-Path $dest "inference\clicker.py")) {
    Write-Host "isegm already present at $dest - skipping."
    exit 0
}
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }

$headers = @{ 'User-Agent' = 'sam3-plugin' }
$tree = Invoke-RestMethod -Uri 'https://api.github.com/repos/cnr-isti-vclab/TagLab/git/trees/main?recursive=1' -Headers $headers
$files = $tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like 'models/isegm/*' }
Write-Host ("files to fetch: " + $files.Count)
if ($files.Count -eq 0) { Write-Error "models/isegm not found in the TagLab repository" }

$done = 0
foreach ($f in $files) {
    $rel = $f.path.Substring('models/isegm/'.Length)
    $out = Join-Path $dest $rel
    $dir = Split-Path $out -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    Invoke-WebRequest -Uri ('https://raw.githubusercontent.com/cnr-isti-vclab/TagLab/main/' + $f.path) -OutFile $out -Headers $headers
    $done++
    if ($done % 20 -eq 0) { Write-Host "  $done / $($files.Count)" }
}
Write-Host "downloaded $done files"

# TagLab namespaced the package as models.isegm - rewrite to plain isegm.
$count = 0
Get-ChildItem $dest -Recurse -Filter *.py | ForEach-Object {
    $t = [IO.File]::ReadAllText($_.FullName)
    if ($t -match 'models\.isegm') {
        $t = $t -replace 'models\.isegm', 'isegm'
        [IO.File]::WriteAllText($_.FullName, $t)
        $count++
    }
}
Write-Host "rewrote imports in $count files"
if (-not (Test-Path (Join-Path $dest "inference\clicker.py"))) {
    Write-Error "isegm download incomplete (inference\clicker.py missing)"
}
Write-Host "[ OK ] isegm installed at $dest"
