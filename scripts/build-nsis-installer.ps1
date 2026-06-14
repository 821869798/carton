param(
    [Parameter(Mandatory = $true)]
    [string]$AppName,

    [Parameter(Mandatory = $true)]
    [string]$AppId,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$MainExe,

    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [Parameter(Mandatory = $false)]
    [string]$OutputFileName,

    [Parameter(Mandatory = $false)]
    [string]$AppDataDirName,

    [Parameter(Mandatory = $false)]
    [string]$Publisher,

    [Parameter(Mandatory = $false)]
    [string]$ProductRegKey,

    [Parameter(Mandatory = $false)]
    [string]$IconPath,

    [Parameter(Mandatory = $false)]
    [string]$InstallDir
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path "$scriptDir\..").Path
$installerScript = Join-Path $repoRoot "scripts\installer\windows\carton-installer.nsi"

if (-not (Test-Path $installerScript)) {
    throw "NSIS installer script not found: $installerScript"
}

$publishDirResolved = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $publishDirResolved $MainExe))) {
    throw "Main executable not found in publish directory: $publishDirResolved\$MainExe"
}

if (-not (Test-Path (Join-Path $publishDirResolved "carton-helper.exe"))) {
    throw "carton-helper.exe not found in publish directory: $publishDirResolved\carton-helper.exe"
}

$outputDirResolved = (Resolve-Path $OutputDir -ErrorAction SilentlyContinue)
if (-not $outputDirResolved) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $outputDirResolved = (Resolve-Path $OutputDir).Path
} else {
    $outputDirResolved = $outputDirResolved.Path
}

if ([string]::IsNullOrWhiteSpace($OutputFileName)) {
    $OutputFileName = "$AppName-Setup.exe"
}

$outputPath = Join-Path $outputDirResolved $OutputFileName

$makeNsis = Get-Command makensis -ErrorAction SilentlyContinue
if (-not $makeNsis) {
    $candidate = "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    if (Test-Path $candidate) {
        $makeNsis = Get-Item $candidate
    }
}

if (-not $makeNsis) {
    throw "makensis not found. Install NSIS (https://nsis.sourceforge.io/Download) and ensure makensis is in PATH."
}

$defines = @(
    "/DAPP_NAME=$AppName",
    "/DAPP_ID=$AppId",
    "/DAPP_VERSION=$Version",
    "/DMAIN_EXE=$MainExe",
    "/DPUBLISH_DIR=$publishDirResolved",
    "/DOUTPUT_EXE=$outputPath"
)

if (-not [string]::IsNullOrWhiteSpace($AppDataDirName)) {
    $defines += "/DAPPDATA_DIR_NAME=$AppDataDirName"
}

if (-not [string]::IsNullOrWhiteSpace($Publisher)) {
    $defines += "/DAPP_PUBLISHER=$Publisher"
}

if (-not [string]::IsNullOrWhiteSpace($ProductRegKey)) {
    $defines += "/DPRODUCT_REG_KEY=$ProductRegKey"
}

if (-not [string]::IsNullOrWhiteSpace($IconPath)) {
    $iconPathResolved = (Resolve-Path $IconPath).Path
    $defines += "/DAPP_ICON=$iconPathResolved"
}

if (-not [string]::IsNullOrWhiteSpace($InstallDir)) {
    $defines += "/DINSTALL_DIR=$InstallDir"
}

Write-Host "Building NSIS installer:"
Write-Host "  Script: $installerScript"
Write-Host "  Output: $outputPath"

& $makeNsis @defines $installerScript
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    throw "makensis failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $outputPath)) {
    throw "NSIS output not found: $outputPath"
}

Write-Host "NSIS installer created: $outputPath"
