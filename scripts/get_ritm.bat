@echo off
REM ============================================================
REM  Enables the RITM engine (the SAME network TagLab uses for
REM  its Positive/Negative Clicks tool). Downloads:
REM   1. isegm inference code  (official RITM repository)
REM   2. ritm_corals.pth       (TagLab's coral-finetuned weights)
REM   3. pip deps into sam3_env (opencv-python, easydict)
REM  and switches the add-in config to engine = "ritm".
REM ============================================================
setlocal
set "REPO_DIR=%~dp0.."
for %%I in ("%REPO_DIR%") do set "REPO_DIR=%%~fI"

set "ENV_PY=%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe"
set "ISEGM_DIR=%REPO_DIR%\python_server\isegm"
set "MODELS_DIR=%REPO_DIR%\models"
set "CKPT=%MODELS_DIR%\ritm_corals.pth"
set "CFG=%LOCALAPPDATA%\SAM3Interactive\config.json"
set "CKPT_URL=http://taglab.isti.cnr.it/models/ritm_corals.pth"

echo.
echo === Step 1/4 : Download RITM code (isegm, from TagLab) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch_isegm.ps1"
if errorlevel 1 (
    echo [ERROR] Could not download the RITM code.
    pause
    exit /b 1
)

echo.
echo === Step 2/4 : Download TagLab checkpoint (ritm_corals.pth) ===
if exist "%CKPT%" (
    echo Checkpoint already present - skipping.
) else (
    if not exist "%MODELS_DIR%" mkdir "%MODELS_DIR%"
    powershell -NoProfile -Command ^
      "Invoke-WebRequest -Uri '%CKPT_URL%' -OutFile '%CKPT%'"
    if errorlevel 1 (
        echo [ERROR] Could not download %CKPT_URL%
        echo         Download it manually into %MODELS_DIR%\
        pause
        exit /b 1
    )
    echo [ OK ] Checkpoint saved to %CKPT%
)

echo.
echo === Step 3/4 : Install pip dependencies into sam3_env ===
if exist "%ENV_PY%" (
    "%ENV_PY%" -m pip install opencv-python easydict
) else (
    echo [WARN] sam3_env python not found at %ENV_PY%
    echo        Run: pip install opencv-python easydict  inside sam3_env.
)

echo.
echo === Step 4/4 : Switch add-in config to the RITM engine ===
if exist "%CFG%" (
    powershell -NoProfile -Command ^
      "$c = Get-Content '%CFG%' -Raw | ConvertFrom-Json;" ^
      "$c | Add-Member -Force NoteProperty engine 'ritm';" ^
      "$c | Add-Member -Force NoteProperty ritm_checkpoint '%CKPT%';" ^
      "$c | ConvertTo-Json | Set-Content -Encoding utf8 '%CFG%'"
    echo [ OK ] %CFG% updated: engine = ritm
) else (
    echo [WARN] %CFG% not found - run scripts\install_addin_config.bat
    echo        first, then re-run this script.
)

echo.
echo Done. Restart the SAM server in ArcGIS Pro (Stop + Start) to
echo switch engines. To go back to SAM, set "engine": "sam" in the
echo config (SAM3 ribbon - Server Settings).
pause
