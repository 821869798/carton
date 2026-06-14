param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipKernel
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path "$scriptDir\..").Path
$appName = "carton"
$guiProject = "$repoRoot\src\carton.GUI\carton.GUI.csproj"
$helperProject = "$repoRoot\src\carton.Helper"
$publishDir = "$repoRoot\artifacts\publish\$Rid-portable"
$helperDir = "$repoRoot\artifacts\publish\$Rid-helper"
$packDir = "$repoRoot\artifacts\pack\$Rid-portable"
$includeKernelScript = "$repoRoot\scripts\include-singbox-kernel.ps1"

[xml]$csproj = Get-Content $guiProject
$version = $csproj.Project.PropertyGroup.Version | Select-Object -First 1

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

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $helperDir) { Remove-Item -Recurse -Force $helperDir }
if (Test-Path $packDir) { Remove-Item -Recurse -Force $packDir }

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $helperDir -Force | Out-Null
New-Item -ItemType Directory -Path $packDir -Force | Out-Null

Write-Host "Publishing $appName portable app ($Rid, $Configuration)..."
dotnet publish $guiProject `
    -c $Configuration `
    -r $Rid `
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
    throw "Portable app publish failed."
}

Write-Host "Publishing carton-helper ($Rid, $Configuration)..."
$cargoTarget = Get-CargoTargetForRid $Rid
if (Get-Command rustup -ErrorAction SilentlyContinue) {
    rustup target add $cargoTarget
}
Push-Location $helperProject
cargo build --release --target $cargoTarget
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) {
    Pop-Location
    throw "carton-helper publish failed."
}
Pop-Location

Copy-Item -LiteralPath (Join-Path $helperProject "target\$cargoTarget\release\carton-helper.exe") -Destination $helperDir -Force

Copy-Item -LiteralPath (Join-Path $helperDir "carton-helper.exe") -Destination $publishDir -Force

if ($SkipKernel) {
    Write-Host "Skipping built-in sing-box runtime."
}
else {
    Write-Host "Including built-in sing-box runtime..."
    & $includeKernelScript -Rid $Rid -Destination $publishDir
}

Get-ChildItem -Path $publishDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $helperDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$stageDir = Join-Path $env:TEMP ("carton-portable-stage-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
try {
    Copy-Item -Path "$publishDir\*" -Destination $stageDir -Recurse -Force
    New-Item -ItemType File -Path (Join-Path $stageDir ".carton_portable_data") -Force | Out-Null

    $portableName = "$appName-$version-$Rid-portable.zip"
    $portablePath = Join-Path $packDir $portableName
    $items = Get-ChildItem -Path $stageDir -Force | Select-Object -ExpandProperty FullName
    if (-not $items) {
        throw "Portable staging directory is empty: $stageDir"
    }

    Compress-Archive -LiteralPath $items -DestinationPath $portablePath -Force
    Write-Host "Portable package created: $portablePath"
}
finally {
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
}
