@echo off
setlocal enabledelayedexpansion

REM Usage: scripts\publish-win-aot.bat [rid] [configuration]
REM Example: scripts\publish-win-aot.bat win-x64 Release

set RID=%1
if "%RID%"=="" set RID=win-x64

set CONFIG=%2
if "%CONFIG%"=="" set CONFIG=Release

set SCRIPT_DIR=%~dp0
set REPO_ROOT=%SCRIPT_DIR%..
set PROJECT=%REPO_ROOT%\src\carton.GUI\carton.GUI.csproj
set OUTPUT=%REPO_ROOT%\artifacts\publish\%RID%

echo Publishing %PROJECT% as %RID% (%CONFIG%) with NativeAOT...
pushd "%REPO_ROOT%"
dotnet publish "%PROJECT%" ^
  -c %CONFIG% ^
  -r %RID% ^
  -o "%OUTPUT%" ^
  /p:PublishAot=true ^
  /p:SelfContained=true ^
  /p:StripSymbols=true ^
  /p:IncludeNativeLibrariesForSelfExtract=true ^
  /p:EnableCompressionInSingleFile=true ^
  /p:InvariantGlobalization=true

if errorlevel 1 (
  echo NativeAOT publish failed.
  popd
  exit /b 1
)

popd
echo Output written to %OUTPUT%
pause
exit /b 0
