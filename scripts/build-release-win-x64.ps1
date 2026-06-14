$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path "$scriptDir\..").Path

$appName = "carton"
$rid = "win-x64"

$csprojPath = "$repoRoot\src\carton.GUI\carton.GUI.csproj"
[xml]$csproj = Get-Content $csprojPath
$Version = $csproj.Project.PropertyGroup.Version | Select-Object -First 1

if ($Version -match "-beta" -or $Version -match "-rc" -or $Version -match "-preview") {
    $Channel = "$rid-beta"
} else {
    $Channel = "$rid-release"
}

$publishDirPortable = "$repoRoot\artifacts\publish\$rid-portable"
$publishDirInstaller = "$repoRoot\artifacts\publish\$rid-installer"
$publishDirHelper = "$repoRoot\artifacts\publish\$rid-helper"
$helperProject = "$repoRoot\src\carton.Helper"
$packDir = "$repoRoot\artifacts\pack\$Channel"
$includeKernelScript = "$repoRoot\scripts\include-singbox-kernel.ps1"
$nsisBuilderScript = "$repoRoot\scripts\build-nsis-installer.ps1"
$kernelStageDir = Join-Path $env:TEMP ("carton-singbox-runtime-" + [Guid]::NewGuid().ToString("N"))

Write-Host "==== Environment ===="
Write-Host "App Name: $appName"
Write-Host "Version:  $Version"
Write-Host "Channel:  $Channel"
Write-Host "RID:      $rid"
Write-Host "Repo Root: $repoRoot"
Write-Host "====================="

Set-Location $repoRoot

$env:DOTNET_ROLL_FORWARD = "Major"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo not found. Install Rust toolchain before building carton-helper."
}

function Get-CargoTargetForRid([string]$Rid) {
    switch ($Rid) {
        "win-x64" { return "x86_64-pc-windows-msvc" }
        "win-arm64" { return "aarch64-pc-windows-msvc" }
        default { throw "Unsupported helper RID: $Rid" }
    }
}

Write-Host "Cleaning up old artifacts..."
if (Test-Path $publishDirPortable) { Remove-Item -Recurse -Force $publishDirPortable }
if (Test-Path $publishDirInstaller) { Remove-Item -Recurse -Force $publishDirInstaller }
if (Test-Path $publishDirHelper) { Remove-Item -Recurse -Force $publishDirHelper }
if (Test-Path $packDir) { Remove-Item -Recurse -Force $packDir }
if (Test-Path $kernelStageDir) { Remove-Item -Recurse -Force $kernelStageDir }

New-Item -ItemType Directory -Path $publishDirPortable -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirInstaller -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirHelper -Force | Out-Null
New-Item -ItemType Directory -Path $packDir -Force | Out-Null
New-Item -ItemType Directory -Path $kernelStageDir -Force | Out-Null

Write-Host "==== 1. Publishing $appName portable ($rid) with NativeAOT ===="

dotnet publish src\carton.GUI\carton.GUI.csproj `
    -c Release `
    -r $rid `
    -o $publishDirPortable `
    /p:PublishAot=true `
    /p:SelfContained=true `
    /p:StripSymbols=true `
    /p:DebugSymbols=false `
    /p:DebugType=None `
    /p:InvariantGlobalization=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Write-Error "Publish failed."
    exit 1
}

Write-Host "Preparing built-in sing-box runtime (single download)..."
& $includeKernelScript -Rid $rid -Destination $kernelStageDir
Copy-Item -Path "$kernelStageDir\*" -Destination $publishDirPortable -Recurse -Force

Write-Host "Publishing carton-helper ($rid) for Windows packages..."
$cargoTarget = Get-CargoTargetForRid $rid
if (Get-Command rustup -ErrorAction SilentlyContinue) {
    rustup target add $cargoTarget
}
Push-Location $helperProject
cargo build --release --target $cargoTarget
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Pop-Location
    Write-Error "carton-helper publish failed."
    exit 1
}
Pop-Location

Copy-Item -LiteralPath "$helperProject\target\$cargoTarget\release\carton-helper.exe" -Destination $publishDirHelper -Force

Copy-Item -LiteralPath "$publishDirHelper\carton-helper.exe" -Destination $publishDirPortable -Force

Write-Host "`n==== 2. Creating Portable Archive ===="
# Remove .pdb files if any
if (Test-Path "$publishDirPortable\*.pdb") {
    Get-ChildItem -Path $publishDirPortable -Filter '*.pdb' -Recurse | Remove-Item -Force
}

$portableName = "$appName-$Version-$rid-portable.zip"
$portablePath = "$packDir\$portableName"
$portableStageDir = Join-Path $env:TEMP ("carton-portable-stage-" + [guid]::NewGuid().ToString("N"))
Write-Host "Compressing to $portablePath..."
New-Item -ItemType Directory -Path $portableStageDir -Force | Out-Null
Copy-Item -Path "$publishDirPortable\*" -Destination $portableStageDir -Recurse -Force
New-Item -ItemType File -Path (Join-Path $portableStageDir ".carton_portable_data") -Force | Out-Null
$portableItems = Get-ChildItem -Path $portableStageDir -Force | Select-Object -ExpandProperty FullName
if (-not $portableItems) {
    throw "Portable staging directory is empty: $portableStageDir"
}
Compress-Archive -LiteralPath $portableItems -DestinationPath $portablePath -Force
if (Test-Path $portableStageDir) { Remove-Item -Recurse -Force $portableStageDir }
Write-Host "Portable archive created successfully."

Write-Host "`n==== 3. Publishing $appName installer ($rid) with NativeAOT ===="
dotnet publish src\carton.GUI\carton.GUI.csproj `
    -c Release `
    -r $rid `
    -o $publishDirInstaller `
    /p:CartonBuildMacro=INSTALLER_BUILD `
    /p:PublishAot=true `
    /p:SelfContained=true `
    /p:StripSymbols=true `
    /p:DebugSymbols=false `
    /p:DebugType=None `
    /p:InvariantGlobalization=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Write-Error "Installer publish failed."
    exit 1
}

Copy-Item -Path "$kernelStageDir\*" -Destination $publishDirInstaller -Recurse -Force
Copy-Item -LiteralPath "$publishDirHelper\carton-helper.exe" -Destination $publishDirInstaller -Force

if (Test-Path "$publishDirInstaller\*.pdb") {
    Get-ChildItem -Path $publishDirInstaller -Filter '*.pdb' -Recurse | Remove-Item -Force
}

Write-Host "`n==== 4. Creating NSIS Installer ===="
$renamedSetupName = "$appName-$Version-$rid-Setup.exe"
& $nsisBuilderScript `
    -AppName $appName `
    -AppId "unifan.carton" `
    -Version $Version `
    -MainExe "$appName.exe" `
    -PublishDir $publishDirInstaller `
    -OutputDir $packDir `
    -OutputFileName $renamedSetupName `
    -AppDataDirName "Carton" `
    -Publisher "Unifan" `
    -ProductRegKey "Software\Unifan\Carton" `
    -IconPath "$repoRoot\src\carton.GUI\Assets\carton_icon.ico"

Set-Content -Path "$packDir\channel.txt" -Value $Channel

Write-Host "`n==== Build Completed Successfully ===="
Write-Host "Output Directory: $packDir"
Write-Host "- Portable Zip: $portableName"
Write-Host "- NSIS Installer: $renamedSetupName"

if (Test-Path $kernelStageDir) {
    Remove-Item -Recurse -Force $kernelStageDir
}
