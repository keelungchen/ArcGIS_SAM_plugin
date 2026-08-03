@echo off
REM ============================================================
REM  SAM3 Toolbox - environment setup for ArcGIS Pro 3.x
REM  Creates a clone of arcgispro-py3 named "sam3_env" and
REM  installs PyTorch + transformers (SAM 3).
REM  Run this file by double-clicking or from a normal cmd window.
REM ============================================================
setlocal

set PRO_PY_ROOT=%PROGRAMFILES%\ArcGIS\Pro\bin\Python
set CONDA_EXE=%PRO_PY_ROOT%\Scripts\conda.exe
set ENV_NAME=sam3_env
set ENV_DIR=%LOCALAPPDATA%\ESRI\conda\envs\%ENV_NAME%

if not exist "%CONDA_EXE%" (
    echo [ERROR] Could not find ArcGIS Pro conda at:
    echo         %CONDA_EXE%
    echo         Edit PRO_PY_ROOT in this script if ArcGIS Pro is
    echo         installed in a non-default location.
    pause
    exit /b 1
)

echo.
echo === Step 1/4 : Clone arcgispro-py3 as %ENV_NAME% ===
if exist "%ENV_DIR%" (
    echo Environment already exists at %ENV_DIR% - skipping clone.
) else (
    "%CONDA_EXE%" create --name %ENV_NAME% --clone arcgispro-py3 --pinned -y
    if errorlevel 1 (
        echo [ERROR] Cloning failed. Close ArcGIS Pro and try again.
        pause
        exit /b 1
    )
)

set ENV_PY=%ENV_DIR%\python.exe
if not exist "%ENV_PY%" (
    echo [ERROR] python.exe not found in %ENV_DIR%
    pause
    exit /b 1
)

echo.
echo === Step 2/4 : Install PyTorch ===
choice /C GC /M "Install [G]PU (NVIDIA CUDA) or [C]PU-only PyTorch"
if errorlevel 2 (
    "%ENV_PY%" -m pip install torch torchvision
) else (
    "%ENV_PY%" -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu126
)
if errorlevel 1 (
    echo [ERROR] PyTorch install failed. Check your internet connection.
    pause
    exit /b 1
)

echo.
echo === Step 3/4 : Install transformers (SAM 3) + helpers ===
"%ENV_PY%" -m pip install --upgrade "transformers>=4.57" accelerate huggingface_hub pillow scikit-image
if errorlevel 1 (
    echo [ERROR] transformers install failed.
    pause
    exit /b 1
)

echo.
echo === Step 4/4 : Activate %ENV_NAME% for ArcGIS Pro ===
call "%PRO_PY_ROOT%\Scripts\proswap.bat" %ENV_NAME%

echo.
echo ============================================================
echo  Done. NEXT STEPS (see docs\User_Manual.html):
echo   1. Accept the SAM 3 license at
echo      https://huggingface.co/facebook/sam3
echo   2. Login:  "%ENV_PY%" -m huggingface_hub.commands.huggingface_cli login
echo      (or run: hf auth login)
echo   3. Verify: "%ENV_PY%" "%~dp0check_install.py"
echo   4. Restart ArcGIS Pro and add SAM3_Toolbox.pyt in Catalog.
echo ============================================================
pause
