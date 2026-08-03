@echo off
REM ============================================================
REM  ArcGIS SAM plugin - one-click installer entry point.
REM  Double-click this file. It sets up the python environment,
REM  installs the ArcGIS Pro add-in and writes the configuration.
REM  Detailed log: install.log (next to this file).
REM
REM  Optional switches (run from a cmd window):
REM    INSTALL.bat -Recreate   rebuild the python env from scratch
REM    INSTALL.bat -CpuOnly    force CPU-only PyTorch
REM ============================================================
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [FAIL] PROBLEM: Windows PowerShell was not found on this system.
    echo        FIX    : PowerShell ships with Windows 10/11 - repair
    echo                 Windows or run installer\install.ps1 manually.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "installer\install.ps1" %*
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
