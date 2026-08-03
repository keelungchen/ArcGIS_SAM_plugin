# Builds the SAM3 Interactive ArcGIS Pro add-in WITHOUT Visual Studio.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build_addin.ps1
#   powershell ... -File scripts\build_addin.ps1 -Install   (also register in Pro)
#
# Background: 'dotnet build' compiles the add-in fine, but the Esri
# packaging step inside Esri.ArcGISPro.Extensions30.targets uses
# CodeTaskFactory, which only works in Visual Studio's MSBuild. This
# script compiles with 'dotnet build', tolerates that packaging error,
# and then creates the .esriAddinX archive itself (same layout:
# Config.daml + Images\ at the root, assembly output under Install\).

param(
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $repo "csharp_addin\SAM3Interactive"
$proj = Join-Path $projDir "SAM3Interactive.csproj"
$outDir = Join-Path $projDir "bin\Release\net8.0-windows"
$dist = Join-Path $repo "csharp_addin\dist"
$package = Join-Path $dist "SAM3Interactive.esriAddinX"

# --- locate a dotnet WITH an SDK (system-wide or user-scoped) ---------
$dotnet = $null
$candidates = @()
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $candidates += $cmd.Source }
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $candidates += $userDotnet }
foreach ($cand in $candidates) {
    $sdks = & $cand --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $cand; break }
}
if ($dotnet -eq $userDotnet) {
    # user-scoped SDK: make sure child processes resolve it
    $env:DOTNET_ROOT = Split-Path $userDotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}
if (-not $dotnet) {
    Write-Error ("No .NET SDK found. Install .NET 8 SDK from " +
        "https://dotnet.microsoft.com/download or run: " +
        "iwr https://dot.net/v1/dotnet-install.ps1 -OutFile di.ps1; " +
        ".\di.ps1 -Channel 8.0")
}
Write-Host "Using dotnet: $dotnet"

# --- compile ----------------------------------------------------------
$buildStart = Get-Date
Write-Host "Compiling (packaging errors from the Esri targets are expected and handled below) ..."
& $dotnet build $proj -c Release 2>&1 | ForEach-Object { "$_" } |
    Where-Object { $_ -notmatch "MSB4801|MSB4036" } | Write-Host
$dll = Join-Path $outDir "SAM3Interactive.dll"
if (-not (Test-Path $dll) -or
    (Get-Item $dll).LastWriteTime -lt $buildStart) {
    Write-Error ("Compilation failed - SAM3Interactive.dll was not " +
        "produced. Scroll up for C# compiler errors.")
}
Write-Host "Compiled OK: $dll"

# --- package (.esriAddinX = zip: Config.daml, Images\, Install\) ------
$stage = Join-Path $env:TEMP ("sam3_addin_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "Install") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "Images") | Out-Null

Copy-Item (Join-Path $projDir "Config.daml") $stage
Copy-Item (Join-Path $projDir "Images\AddinDesktop32.png") (Join-Path $stage "Images")
Copy-Item (Join-Path $outDir "SAM3Interactive.dll") (Join-Path $stage "Install")
$deps = Join-Path $outDir "SAM3Interactive.deps.json"
if (Test-Path $deps) { Copy-Item $deps (Join-Path $stage "Install") }
$pdb = Join-Path $outDir "SAM3Interactive.pdb"
if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $stage "Install") }

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
if (Test-Path $package) { Remove-Item $package -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $package)
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "[ OK ] Add-in package created:"
Write-Host "       $package"

# --- optional: register with ArcGIS Pro -------------------------------
if ($Install) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "ArcGIS\shared\bin\RegisterAddIn.exe"),
        "C:\Program Files\Common Files\ArcGIS\shared\bin\RegisterAddIn.exe",
        "C:\Program Files\ArcGIS\Pro\bin\RegisterAddIn.exe"
    )
    $reg = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($reg) {
        & $reg $package /s
        Write-Host "[ OK ] Registered with ArcGIS Pro ($reg)."
    } else {
        Write-Host "[WARN] RegisterAddIn.exe not found - double-click the .esriAddinX to install."
    }
} else {
    Write-Host "       Double-click it to install into ArcGIS Pro"
    Write-Host "       (or re-run with -Install)."
}
