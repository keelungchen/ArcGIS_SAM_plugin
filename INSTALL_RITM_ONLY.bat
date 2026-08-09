@echo off
REM ============================================================
REM  ArcGIS SAM plugin - RITM-ONLY installer entry point.
REM  Double-click this file for the slim edition:
REM
REM    installed : the interactive Click Segment add-in running
REM                TagLab's RITM coral network (models\ritm_corals.pth,
REM                already included - no download, no account needed)
REM    skipped   : the SAM packages (transformers, accelerate,
REM                huggingface_hub) and the SAM geoprocessing toolbox
REM
REM  Everything else is identical to INSTALL.bat, including the
REM  sam3_env python environment and PyTorch (RITM needs them too).
REM  Detailed log: install.log (next to this file).
REM
REM  Optional switches (run from a cmd window):
REM    INSTALL_RITM_ONLY.bat -Recreate   rebuild the python env
REM    INSTALL_RITM_ONLY.bat -CpuOnly    force CPU-only PyTorch
REM ============================================================
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [FAIL] PROBLEM: Windows PowerShell was not found on this system.
    echo        FIX    : PowerShell ships with Windows 10/11 - repair
    echo                 Windows or run installer\install.ps1 -RitmOnly
    echo                 manually.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "installer\install.ps1" -RitmOnly %*
set RC=%ERRORLEVEL%

echo.
if "%RC%"=="0" (
    echo Done. You can close this window.
) else (
    echo Finished with problems - scroll up for the PROBLEM/FIX lines,
    echo or open install.log next to this file.
)
pause
exit /b %RC%
