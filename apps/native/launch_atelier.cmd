@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%\..\.."
if "%ATELIER_PYTHON%"=="" set "ATELIER_PYTHON=python"
set "PYTHONPATH=%ROOT%;%PYTHONPATH%"
cd /d "%ROOT%"
"%ATELIER_PYTHON%" -m atelier %*
exit /b %ERRORLEVEL%
