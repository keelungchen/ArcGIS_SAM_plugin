@echo off
REM Writes the SAM3 Interactive add-in configuration file:
REM   %LOCALAPPDATA%\SAM3Interactive\config.json
REM pointing at this repository's python_server\sam_server.py and the
REM sam3_env conda environment. Run this once after installing the
REM add-in (SAM3Interactive.esriAddinX).

setlocal
set "REPO_DIR=%~dp0.."
for %%I in ("%REPO_DIR%") do set "REPO_DIR=%%~fI"

set "PYTHON_EXE=%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe"
set "SERVER_SCRIPT=%REPO_DIR%\python_server\sam_server.py"
set "CFG_DIR=%LOCALAPPDATA%\SAM3Interactive"

if not exist "%PYTHON_EXE%" (
    echo [WARN] sam3_env python not found at:
    echo        %PYTHON_EXE%
    echo        Run scripts\setup_env.bat first, or edit the generated
    echo        config.json and fix "python_exe" manually.
)
if not exist "%SERVER_SCRIPT%" (
    echo [ERROR] server script not found: %SERVER_SCRIPT%
    pause
    exit /b 1
)

if not exist "%CFG_DIR%" mkdir "%CFG_DIR%"

powershell -NoProfile -Command ^
  "$cfg = [ordered]@{ python_exe = '%PYTHON_EXE%'; server_script = '%SERVER_SCRIPT%'; port = 8765; engine = 'sam'; model_id = 'facebook/sam2.1-hiera-tiny'; ritm_checkpoint = '%REPO_DIR%\models\ritm_corals.pth'; max_image_size = 2048 };" ^
  "$cfg | ConvertTo-Json | Set-Content -Encoding utf8 '%CFG_DIR%\config.json'"

if errorlevel 1 (
    echo [ERROR] failed to write config.json
    pause
    exit /b 1
)

echo.
echo [ OK ] Configuration written to:
echo        %CFG_DIR%\config.json
echo.
type "%CFG_DIR%\config.json"
echo.
pause
