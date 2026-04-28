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
$packDir = "$repoRoot\artifacts\pack\$Channel"

Write-Host "==== Environment ===="
Write-Host "App Name: $appName"
Write-Host "Version:  $Version"
Write-Host "Channel:  $Channel"
Write-Host "RID:      $rid"
Write-Host "Repo Root: $repoRoot"
Write-Host "====================="

Set-Location $repoRoot

$env:DOTNET_ROLL_FORWARD = "Major"

# Check for Velopack CLI
if (!(Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Velopack CLI (vpk) not found in current PATH. Trying to install/update..."
    dotnet tool update --global vpk --version 0.0.1298
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
        Write-Warning "Failed to install Velopack CLI automatically."
    }
    # Add to path for this session just in case
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

Write-Host "Cleaning up old artifacts..."
if (Test-Path $publishDirPortable) { Remove-Item -Recurse -Force $publishDirPortable }
if (Test-Path $publishDirInstaller) { Remove-Item -Recurse -Force $publishDirInstaller }
if (Test-Path $packDir) { Remove-Item -Recurse -Force $packDir }

New-Item -ItemType Directory -Path $publishDirPortable -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirInstaller -Force | Out-Null
New-Item -ItemType Directory -Path $packDir -Force | Out-Null

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

if (Test-Path "$publishDirInstaller\*.pdb") {
    Get-ChildItem -Path $publishDirInstaller -Filter '*.pdb' -Recurse | Remove-Item -Force
}

Write-Host "`n==== 4. Creating Velopack Installer ===="
$iconPath = (Resolve-Path "src\carton.GUI\Assets\carton_icon.ico").Path
$mainExe = "$appName.exe"

vpk pack `
    --packId $appName `
    --packVersion $Version `
    --channel $Channel `
    --runtime $rid `
    --packDir $publishDirInstaller `
    --mainExe $mainExe `
    --packTitle $appName `
    --icon $iconPath `
    --outputDir $packDir

if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Write-Error "Velopack packaging failed."
    exit 1
}

Set-Content -Path "$packDir\channel.txt" -Value $Channel

$generatedSetupFile = Get-ChildItem -Path $packDir -Filter "*-Setup.exe" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$renamedSetupName = "$appName-$Version-$rid-Setup.exe"
$renamedSetupPath = "$packDir\$renamedSetupName"

if (-not $generatedSetupFile) {
    Write-Error "Installer setup executable was not generated in: $packDir"
    exit 1
}

if (-not [string]::Equals($generatedSetupFile.Name, $renamedSetupName, [System.StringComparison]::OrdinalIgnoreCase)) {
    # Delete if target already exists to prevent Rename-Item from failing
    if (Test-Path $renamedSetupPath) {
        Remove-Item -Path $renamedSetupPath -Force
    }
    Rename-Item -Path $generatedSetupFile.FullName -NewName $renamedSetupName -Force
}

Write-Host "`n==== Build Completed Successfully ===="
Write-Host "Output Directory: $packDir"
Write-Host "- Portable Zip: $portableName"
Write-Host "- Velopack Installer: $renamedSetupName"
