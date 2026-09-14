param(
    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"

function Get-AssetCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory = $true)]
        [string]$VersionWithoutPrefix
    )

    switch ($RuntimeIdentifier) {
        "win-x64" {
            return @(
                "sing-box-$VersionWithoutPrefix-windows-amd64.zip",
                "sing-box-$VersionWithoutPrefix-windows-amd64v3.zip"
            )
        }
        "win-arm64" {
            return @(
                "sing-box-$VersionWithoutPrefix-windows-arm64.zip"
            )
        }
        default {
            throw "Unsupported RID for built-in sing-box kernel: $RuntimeIdentifier"
        }
    }
}

if (-not (Test-Path -LiteralPath $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$latestReleaseUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
$headers = @{
    "User-Agent" = "carton-build-script"
    "Accept" = "application/vnd.github+json"
}

Write-Host "Resolving latest sing-box release for $Rid..."
$release = Invoke-RestMethod -Uri $latestReleaseUrl -Headers $headers
$tag = [string]$release.tag_name
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "GitHub latest release response does not include tag_name."
}

$version = $tag.TrimStart("v")
$candidates = Get-AssetCandidates -RuntimeIdentifier $Rid -VersionWithoutPrefix $version
$asset = $null

foreach ($candidate in $candidates) {
    $asset = $release.assets | Where-Object { $_.name -eq $candidate } | Select-Object -First 1
    if ($asset) {
        break
    }
}

if (-not $asset) {
    $assetList = ($release.assets | Select-Object -ExpandProperty name) -join ", "
    throw "No matching sing-box asset found for $Rid in release $tag. Assets: $assetList"
}

$tempRoot = Join-Path $env:TEMP ("carton-singbox-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $asset.name
$extractDir = Join-Path $tempRoot "extract"

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

try {
    Write-Host "Downloading $($asset.name) from $tag..."
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath

    Write-Host "Extracting sing-box package..."
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force

    $kernelFile = Get-ChildItem -Path $extractDir -Recurse -File -Filter "sing-box.exe" | Select-Object -First 1
    if (-not $kernelFile) {
        throw "sing-box.exe was not found in downloaded asset: $($asset.name)"
    }

    $runtimeFiles = Get-ChildItem -Path $extractDir -Recurse -File | Where-Object {
        $_.Name -ieq "sing-box.exe" -or
        $_.Extension -ieq ".dll" -or
        $_.Name -match "\.so(?:\..+)?$"
    }

    if (-not $runtimeFiles) {
        throw "No runtime files (sing-box.exe/*.dll/*.so*) were found in extracted package."
    }

    foreach ($file in $runtimeFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name) -Force
    }

    Write-Host "Included built-in sing-box kernel $tag into: $Destination"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
