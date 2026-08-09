# ============================================================
#  ArcGIS SAM plugin - one-click installer
#
#  Run via INSTALL.bat (double-click). Performs, in order:
#    1. Locate ArcGIS Pro and check its version
#    2. Check free disk space
#    3. Create the 'sam3_env' conda environment (clone of
#       arcgispro-py3) unless it already exists
#    4. Install PyTorch (auto GPU/CPU detection)
#    5. Install the remaining python packages
#    6. Copy the runtime files to %LOCALAPPDATA%\SAM3Interactive\app
#    7. Write the add-in configuration (free port auto-picked)
#    8. Install the ArcGIS Pro add-in (.esriAddinX)
#    9. Validate the python environment
#
#  Every failure prints a PROBLEM line and a FIX line and is
#  repeated in the final summary. Full log: install.log next to
#  INSTALL.bat.
#
#  Options:  -Recreate   delete + rebuild sam3_env from scratch
#            -CpuOnly    force CPU-only PyTorch
#            -RitmOnly   install the RITM click engine only: skips the
#                        SAM packages (transformers / accelerate /
#                        huggingface_hub) and the SAM geoprocessing
#                        toolbox. The add-in then offers RITM only.
# ============================================================

param(
    [switch]$Recreate,
    [switch]$CpuOnly,
    [switch]$RitmOnly
)

$ErrorActionPreference = 'Continue'

$PackageRoot = Split-Path -Parent $PSScriptRoot
$LogPath     = Join-Path $PackageRoot 'install.log'
$EnvName     = 'sam3_env'
$EnvDir      = Join-Path $env:LOCALAPPDATA "ESRI\conda\envs\$EnvName"
$EnvPy       = Join-Path $EnvDir 'python.exe'
$CfgDir      = Join-Path $env:LOCALAPPDATA 'SAM3Interactive'
$AppDir      = Join-Path $CfgDir 'app'

$script:Problems = New-Object System.Collections.ArrayList
$script:Warnings = New-Object System.Collections.ArrayList

try { Start-Transcript -Path $LogPath -Force | Out-Null } catch {}

function Info($msg)  { Write-Host $msg }
function Ok($msg)    { Write-Host "[ OK ] $msg" -ForegroundColor Green }
function Warn2($msg) {
    Write-Host "[WARN] $msg" -ForegroundColor Yellow
    [void]$script:Warnings.Add($msg)
}
function Step($msg)  { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# Print a problem + how to fix it; when -Fatal, stop the installer.
function Problem($what, $fix, [switch]$Fatal) {
    Write-Host "[FAIL] PROBLEM: $what" -ForegroundColor Red
    Write-Host "       FIX    : $fix"  -ForegroundColor Yellow
    [void]$script:Problems.Add("$what`n         FIX: $fix")
    if ($Fatal) { Finish 1 }
}

function Finish($code) {
    Write-Host ""
    Write-Host "============================================================"
    if ($script:Problems.Count -eq 0 -and $code -eq 0) {
        Write-Host " INSTALLATION SUCCESSFUL" -ForegroundColor Green
        Write-Host ""
        Write-Host " Next steps:"
        Write-Host "   1. Start ArcGIS Pro and open a project."
        Write-Host "   2. The 'SAM Segmentation' ribbon tab should appear."
        Write-Host "      (If not: Settings > Add-In Manager and check that"
        Write-Host "      SAM Interactive Segmentation is listed.)"
        Write-Host "   3. Nothing else to do: the inference server starts"
        Write-Host "      itself ~10 s after Pro launches and warms the"
        Write-Host "      model in the background ('Start Server' on the"
        Write-Host "      ribbon triggers it manually if you want)."
        Write-Host "   4. Manual: docs\User_Manual.html (in the install"
        Write-Host "      folder: $AppDir)"
    } else {
        Write-Host " INSTALLATION FINISHED WITH PROBLEMS" -ForegroundColor Red
        $i = 0
        foreach ($p in $script:Problems) {
            $i++
            Write-Host ""
            Write-Host " Problem ${i}: $p" -ForegroundColor Red
        }
        Write-Host ""
        Write-Host " Fix the problem(s) above and double-click INSTALL.bat"
        Write-Host " again - completed steps are skipped automatically."
    }
    if ($script:Warnings.Count -gt 0) {
        Write-Host ""
        Write-Host " Warnings (not blocking):" -ForegroundColor Yellow
        foreach ($w in $script:Warnings) { Write-Host "   - $w" }
    }
    Write-Host ""
    Write-Host " Full log: $LogPath"
    Write-Host "============================================================"
    try { Stop-Transcript | Out-Null } catch {}
    exit $code
}

Info "ArcGIS SAM plugin installer"
Info "Package folder : $PackageRoot"
Info "Install target : $AppDir"
if ($RitmOnly) {
    Info "Edition        : RITM only (no SAM packages, no SAM toolbox)"
}

# ------------------------------------------------------------
Step "Step 1/9 : Locate ArcGIS Pro"
# ------------------------------------------------------------
$proInstall = $null
try {
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\ESRI\ArcGISPro' -ErrorAction Stop
    $proInstall = $reg.InstallDir
} catch {}
if (-not $proInstall) {
    $guess = Join-Path $env:ProgramFiles 'ArcGIS\Pro'
    if (Test-Path (Join-Path $guess 'bin\ArcGISPro.exe')) { $proInstall = $guess }
}
if (-not $proInstall -or -not (Test-Path (Join-Path $proInstall 'bin\ArcGISPro.exe'))) {
    Problem -Fatal `
        "ArcGIS Pro was not found on this computer." `
        "Install ArcGIS Pro 3.6 (or newer) first, then run INSTALL.bat again."
}
Ok "ArcGIS Pro found: $proInstall"

$proVersion = $null
try {
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\ESRI\ArcGISPro' -ErrorAction Stop
    $proVersion = $reg.RealVersion
} catch {}
if ($proVersion) {
    Info "ArcGIS Pro version: $proVersion"
    try {
        if ([version]$proVersion -lt [version]'3.6') {
            Problem -Fatal `
                "This add-in requires ArcGIS Pro 3.6+, but version $proVersion is installed." `
                "Upgrade ArcGIS Pro, or rebuild the add-in for your version (edit desktopVersion in Config.daml and scripts\build_addin.ps1 on the development machine)."
        }
    } catch {}
} else {
    Warn2 "Could not read the ArcGIS Pro version - continuing; the add-in needs Pro 3.6+."
}

$Conda = Join-Path $proInstall 'bin\Python\Scripts\conda.exe'
if (-not (Test-Path $Conda)) {
    Problem -Fatal `
        "ArcGIS Pro's conda was not found at: $Conda" `
        "Your ArcGIS Pro installation seems incomplete (no Python). Re-run the ArcGIS Pro setup and include the Python environment."
}
Ok "conda found: $Conda"

# ------------------------------------------------------------
Step "Step 2/9 : Check disk space"
# ------------------------------------------------------------
$drive = (Get-Item $env:LOCALAPPDATA).PSDrive.Name
$freeGB = [math]::Round((Get-PSDrive $drive).Free / 1GB, 1)
Info "Free space on drive ${drive}: $freeGB GB"
if ($freeGB -lt 5) {
    Problem -Fatal `
        "Only $freeGB GB free on drive ${drive}: - the python environment needs about 8-12 GB." `
        "Free up disk space on drive ${drive}: and run INSTALL.bat again."
} elseif ($freeGB -lt 12) {
    Warn2 "Less than 12 GB free on drive ${drive}: - the install may run out of space (env clone + PyTorch)."
} else {
    Ok "Disk space looks sufficient."
}

# ------------------------------------------------------------
Step "Step 3/9 : Python environment ($EnvName)"
# ------------------------------------------------------------
if ($Recreate -and (Test-Path $EnvDir)) {
    Info "-Recreate given: removing the existing environment ..."
    & $Conda env remove --name $EnvName -y
    if (Test-Path $EnvDir) {
        try { Remove-Item -Recurse -Force $EnvDir -ErrorAction Stop } catch {}
    }
}

if (Test-Path $EnvPy) {
    Ok "Environment already exists - reusing it ($EnvDir)."
    Info "(Use 'INSTALL.bat -Recreate' to rebuild it from scratch.)"
} else {
    $proRunning = Get-Process -Name 'ArcGISPro' -ErrorAction SilentlyContinue
    if ($proRunning) {
        Problem -Fatal `
            "ArcGIS Pro is currently running - the environment clone would fail or corrupt." `
            "Close ArcGIS Pro completely, then double-click INSTALL.bat again."
    }
    Info "Cloning arcgispro-py3 as $EnvName - this takes 5-20 minutes, please wait ..."
    & $Conda create --name $EnvName --clone arcgispro-py3 --pinned -y
    if (-not (Test-Path $EnvPy)) {
        Problem -Fatal `
            "The conda clone failed (python.exe missing in $EnvDir)." `
            "Common causes: ArcGIS Pro still running, antivirus/OneDrive locking files, or low disk space. Fix and re-run; if it persists, run as the same Windows user that installed ArcGIS Pro."
    }
    Ok "Environment created: $EnvDir"
}

# Stop a leftover SAM server so pip can update files.
$serverProcs = Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -like "$EnvDir*" }
foreach ($p in $serverProcs) {
    Warn2 "Stopped a running sam3_env python process (PID $($p.ProcessId)) so packages can be updated."
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------
Step "Step 4/9 : PyTorch"
# ------------------------------------------------------------
& $EnvPy -c "import torch" 2>$null
if ($LASTEXITCODE -eq 0) {
    Ok "PyTorch already installed - skipping."
} else {
    $hasNvidia = $false
    if (-not $CpuOnly) {
        try {
            $gpus = Get-CimInstance Win32_VideoController -ErrorAction Stop
            foreach ($g in $gpus) {
                if ($g.Name -match 'NVIDIA') { $hasNvidia = $true }
            }
        } catch {}
    }
    if ($hasNvidia) {
        Info "NVIDIA GPU detected - installing CUDA PyTorch (~3 GB download) ..."
        & $EnvPy -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu126
    } else {
        Info "No NVIDIA GPU detected (or -CpuOnly) - installing CPU PyTorch ..."
        & $EnvPy -m pip install torch torchvision
    }
    if ($LASTEXITCODE -ne 0) {
        Problem -Fatal `
            "The PyTorch installation failed." `
            "Check the internet connection / proxy / firewall and re-run INSTALL.bat. On a proxy, set HTTPS_PROXY before running. To force the smaller CPU build: INSTALL.bat -CpuOnly"
    }
    Ok "PyTorch installed."
}

# ------------------------------------------------------------
Step "Step 5/9 : Python packages"
# ------------------------------------------------------------
if ($RitmOnly) {
    # RITM needs torch + opencv + easydict only; transformers,
    # accelerate and huggingface_hub are for the SAM engine.
    Info "RITM-only edition: skipping transformers / accelerate / huggingface_hub."
    & $EnvPy -m pip install --upgrade pillow scikit-image opencv-python easydict
} else {
    & $EnvPy -m pip install --upgrade "transformers>=4.57" accelerate huggingface_hub pillow scikit-image opencv-python easydict
}
if ($LASTEXITCODE -ne 0) {
    Problem -Fatal `
        "pip failed to install the required packages." `
        "Check the internet connection and re-run the installer. Details are in $LogPath."
}
if ($RitmOnly) {
    Ok "Packages installed (scikit-image, opencv, easydict)."
} else {
    Ok "Packages installed (transformers, scikit-image, opencv, ...)."
}

# ------------------------------------------------------------
Step "Step 6/9 : Copy runtime files"
# ------------------------------------------------------------
New-Item -ItemType Directory -Force $AppDir | Out-Null

function CopyTree($name, [switch]$Mirror) {
    $src = Join-Path $PackageRoot $name
    if (-not (Test-Path $src)) {
        Problem -Fatal `
            "'$name' is missing from this package ($src)." `
            "The package is incomplete - re-create it with scripts\make_package.ps1 on the development machine and copy the new zip over."
    }
    $dst = Join-Path $AppDir $name
    $flags = @('/E', '/NJH', '/NJS', '/NFL', '/NDL', '/XD', '__pycache__')
    if ($Mirror) { $flags = @('/MIR', '/NJH', '/NJS', '/NFL', '/NDL', '/XD', '__pycache__') }
    robocopy $src $dst @flags | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Problem -Fatal `
            "Copying '$name' to $dst failed (robocopy code $LASTEXITCODE)." `
            "Close programs that may lock the folder (ArcGIS Pro, editors, antivirus scan) and re-run INSTALL.bat."
    }
    Ok "$name -> $dst"
}

CopyTree 'python_server' -Mirror
CopyTree 'sam3_tools' -Mirror
CopyTree 'models'
CopyTree 'docs'
# The geoprocessing toolbox runs every tool through the SAM engine, so
# it is left out of the RITM-only edition (it would need transformers).
$files = if ($RitmOnly) { @('README.md') }
         else { @('SAM3_Toolbox.pyt', 'README.md') }
foreach ($f in $files) {
    $src = Join-Path $PackageRoot $f
    if (Test-Path $src) { Copy-Item $src $AppDir -Force }
}
if (-not $RitmOnly) {
    Get-ChildItem (Join-Path $PackageRoot '*.pyt.xml') -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName $AppDir -Force }
}
$LASTEXITCODE = 0

# ------------------------------------------------------------
Step "Step 7/9 : Write the add-in configuration"
# ------------------------------------------------------------
# Pick a free port (another app may already use 8765).
$port = 8765
try {
    $inUse = Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Select-Object -ExpandProperty LocalPort
    while ($inUse -contains $port) { $port++ }
} catch {}
if ($port -ne 8765) {
    Warn2 "Port 8765 is already in use by another program - using port $port instead (written to config.json)."
}

$cfgPath = Join-Path $CfgDir 'config.json'
if (Test-Path $cfgPath) {
    Copy-Item $cfgPath "$cfgPath.bak" -Force
    Info "Existing configuration backed up to config.json.bak"
}
# RITM is the default engine (small, CPU-friendly, no embedding pass,
# so the first click is fast). Without its weights, fall back to SAM.
$ritmCkpt = Join-Path $AppDir 'models\ritm_corals.pth'
if ($RitmOnly -and -not (Test-Path $ritmCkpt)) {
    Problem -Fatal `
        "This is the RITM-only edition, but the RITM weights are missing: $ritmCkpt" `
        "The package is incomplete - re-create it with 'scripts\make_package.ps1 -RitmOnly' (models\ritm_corals.pth must be present), or download the weights from http://taglab.isti.cnr.it/models/ritm_corals.pth into the package's models\ folder and run the installer again."
}
if (Test-Path $ritmCkpt) { $engine = 'ritm' } else { $engine = 'sam' }
$cfg = [ordered]@{
    python_exe        = $EnvPy
    server_script     = (Join-Path $AppDir 'python_server\sam_server.py')
    port              = $port
    engine            = $engine
    model_id          = 'facebook/sam2.1-hiera-tiny'
    ritm_checkpoint   = $ritmCkpt
    max_image_size    = 2048
    auto_start_server = $true
    ritm_only         = [bool]$RitmOnly
}
$cfg | ConvertTo-Json | Set-Content -Encoding utf8 $cfgPath
Ok "Configuration written: $cfgPath"

# ------------------------------------------------------------
Step "Step 8/9 : Install the ArcGIS Pro add-in"
# ------------------------------------------------------------
$addinX = Get-ChildItem (Join-Path $PackageRoot 'csharp_addin\dist') -Filter '*.esriAddinX' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $addinX) {
    $addinX = Get-ChildItem $PackageRoot -Recurse -Filter '*.esriAddinX' -ErrorAction SilentlyContinue |
        Select-Object -First 1
}
if (-not $addinX) {
    Problem -Fatal `
        "SAM3Interactive.esriAddinX was not found in this package." `
        "Re-create the package with scripts\make_package.ps1 (it includes the built add-in) and copy the new zip over."
}

$docsDir  = [Environment]::GetFolderPath('MyDocuments')
$addinDir = Join-Path $docsDir 'ArcGIS\AddIns\ArcGISPro'
try {
    New-Item -ItemType Directory -Force $addinDir | Out-Null
    Copy-Item $addinX.FullName (Join-Path $addinDir $addinX.Name) -Force -ErrorAction Stop
    Ok "Add-in installed to: $addinDir"
    Info "(ArcGIS Pro discovers add-ins in this folder automatically.)"
} catch {
    Problem `
        "Could not copy the add-in to $addinDir ($($_.Exception.Message))." `
        "Close ArcGIS Pro and re-run INSTALL.bat, or simply double-click $($addinX.FullName) and press 'Install Add-In'."
}

# ------------------------------------------------------------
Step "Step 9/9 : Validate the python environment"
# ------------------------------------------------------------
$checkPy = Join-Path $env:TEMP 'sam3_install_check.py'
@'
import sys
app_dir = sys.argv[1]
want_sam = len(sys.argv) < 3 or sys.argv[2] != "ritm-only"
sys.path.insert(0, app_dir)                      # sam3_tools
sys.path.insert(0, app_dir + "\\python_server")  # isegm
errors = 0
def check(name, fn):
    global errors
    try:
        fn()
        print("[ OK ] " + name)
    except Exception as exc:
        errors += 1
        print("[FAIL] " + name + ": " + str(exc))
check("numpy", lambda: __import__("numpy"))
check("pillow", lambda: __import__("PIL"))
check("scikit-image", lambda: __import__("skimage"))
def _torch():
    import torch
    if torch.cuda.is_available():
        print("       GPU: " + torch.cuda.get_device_name(0))
    else:
        print("       No CUDA GPU - inference runs on CPU (slower but works).")
check("torch", _torch)
def _tf():
    import transformers
    if not hasattr(transformers, "Sam2Model"):
        raise RuntimeError("transformers too old - Sam2Model missing")
if want_sam:
    check("transformers + SAM2 classes", _tf)
else:
    print("[SKIP] transformers (RITM-only edition)")
check("cv2 (RITM)", lambda: __import__("cv2"))
check("isegm (RITM engine)", lambda: __import__("isegm"))
sys.exit(errors)
'@ | Set-Content -Encoding ascii $checkPy

$checkMode = if ($RitmOnly) { 'ritm-only' } else { 'full' }
& $EnvPy $checkPy $AppDir $checkMode
if ($LASTEXITCODE -ne 0) {
    Problem `
        "The python environment validation reported failures (see the [FAIL] lines above)." `
        "Re-run INSTALL.bat (it re-installs only what is missing). If a specific package keeps failing, run:  `"$EnvPy`" -m pip install <package>  and check $LogPath."
} else {
    Ok "Python environment validated."
}
Remove-Item $checkPy -ErrorAction SilentlyContinue

if ($script:Problems.Count -gt 0) { Finish 1 } else { Finish 0 }
