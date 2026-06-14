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
set HELPER_PROJECT=%REPO_ROOT%\src\carton.Helper
set OUTPUT=%REPO_ROOT%\artifacts\publish\%RID%
set HELPER_OUTPUT=%REPO_ROOT%\artifacts\publish\%RID%-helper
set CARGO_TARGET=
if /I "%RID%"=="win-x64" set CARGO_TARGET=x86_64-pc-windows-msvc
if /I "%RID%"=="win-arm64" set CARGO_TARGET=aarch64-pc-windows-msvc

where cargo >nul 2>nul
if errorlevel 1 (
  echo cargo not found. Install Rust toolchain before building carton-helper.
  exit /b 1
)

if "%CARGO_TARGET%"=="" (
  echo Unsupported helper RID: %RID%
  exit /b 1
)

where rustup >nul 2>nul
if not errorlevel 1 (
  rustup target add %CARGO_TARGET%
)

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

echo Building carton-helper with Rust (%CONFIG%)...
pushd "%HELPER_PROJECT%"
cargo build --release --target %CARGO_TARGET%
if errorlevel 1 (
  echo carton-helper Rust build failed.
  popd
  popd
  exit /b 1
)
popd

if not exist "%HELPER_OUTPUT%" mkdir "%HELPER_OUTPUT%"
copy /Y "%HELPER_PROJECT%\target\%CARGO_TARGET%\release\carton-helper.exe" "%HELPER_OUTPUT%\" >nul

copy /Y "%HELPER_OUTPUT%\carton-helper.exe" "%OUTPUT%\" >nul
if errorlevel 1 (
  echo Failed to copy carton-helper.exe.
  popd
  exit /b 1
)

popd
echo Output written to %OUTPUT%
pause
exit /b 0
