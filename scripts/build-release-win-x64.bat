@echo off
setlocal enabledelayedexpansion

REM Usage: scripts\build-release-win-x64.bat

set SCRIPT_DIR=%~dp0

powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build-release-win-x64.ps1"

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo.
echo Build success.
pause
exit /b 0
