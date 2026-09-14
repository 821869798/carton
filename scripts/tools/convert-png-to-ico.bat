@echo off
setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set INPUT=..\..\src\carton.GUI\Assets\carton_icon_full.png
set OUTPUT=..\..\src\carton.GUI\Assets\carton_icon.ico

pushd "%SCRIPT_DIR%"
go run convert-png-to-ico.go -in "%INPUT%" -out "%OUTPUT%"

if errorlevel 1 (
  echo ICO conversion failed.
  popd
  exit /b 1
)

popd
echo.
echo ICO conversion success.
pause
exit /b 0
