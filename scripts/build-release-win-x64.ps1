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

$publishDir = "$repoRoot\artifacts\publish\$rid"
$packDir = "$repoRoot\artifacts\pack\$rid-$Channel"

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
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $packDir) { Remove-Item -Recurse -Force $packDir }

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $packDir -Force | Out-Null

Write-Host "==== 1. Publishing $appName ($rid) with NativeAOT ===="

dotnet publish src\carton.GUI\carton.GUI.csproj `
    -c Release `
    -r $rid `
    -o $publishDir `
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
if (Test-Path "$publishDir\*.pdb") {
    Get-ChildItem -Path $publishDir -Filter '*.pdb' -Recurse | Remove-Item -Force
}

$portableName = "$appName-$Version-$rid-portable.zip"
$portablePath = "$packDir\$portableName"
Write-Host "Compressing to $portablePath..."
Compress-Archive -Path "$publishDir\*" -DestinationPath $portablePath -Force
Write-Host "Portable archive created successfully."

Write-Host "`n==== 3. Creating Velopack Installer ===="
$iconPath = (Resolve-Path "src\carton.GUI\Assets\carton_icon.ico").Path
$mainExe = "$appName.exe"

vpk pack `
    --packId $appName `
    --packVersion $Version `
    --channel $Channel `
    --runtime $rid `
    --packDir $publishDir `
    --mainExe $mainExe `
    --packTitle $appName `
    --icon $iconPath `
    --outputDir $packDir

if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Write-Error "Velopack packaging failed."
    exit 1
}

Set-Content -Path "$packDir\channel.txt" -Value $Channel

$generatedSetupFile = Get-ChildItem -Path $packDir -Filter "*-Setup.exe" | Select-Object -First 1
$renamedSetupName = "$appName-$Version-$rid-Setup.exe"
$renamedSetupPath = "$packDir\$renamedSetupName"

if ($generatedSetupFile) {
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
