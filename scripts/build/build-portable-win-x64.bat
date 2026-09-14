@echo off
setlocal

set SCRIPT_DIR=%~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build-portable-win-x64.ps1" %*
set EXIT_CODE=%ERRORLEVEL%
pause
exit /b %EXIT_CODE%
