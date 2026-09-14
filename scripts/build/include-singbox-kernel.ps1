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

$token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { $env:GH_TOKEN } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { $env:GITHUB_TOKEN } else { $null }
if ($token) {
    $headers["Authorization"] = "Bearer $token"
}

Write-Host "Resolving latest sing-box release for $Rid..."
$release = $null
try {
    $release = Invoke-RestMethod -Uri $latestReleaseUrl -Headers $headers -ErrorAction Stop
} catch {
    Write-Warning "Could not query GitHub API for latest release ($($_.Exception.Message)). Attempting fallback to release redirect..."
}

$tag = $null
if ($release -and $release.tag_name) {
    $tag = [string]$release.tag_name
} else {
    try {
        $effectiveUrl = (& curl.exe -sIL -o NUL -w "%{url_effective}" "https://github.com/SagerNet/sing-box/releases/latest" 2>$null).Trim()
        if ($effectiveUrl -match "/tag/([^/]+)/?$") {
            $tag = $matches[1]
        }
    } catch {
        Write-Warning "curl.exe redirect resolution failed: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "GitHub latest release tag could not be resolved."
}

$version = $tag.TrimStart("v")
$candidates = Get-AssetCandidates -RuntimeIdentifier $Rid -VersionWithoutPrefix $version
$selectedCandidate = $null
$downloadUrl = $null

if ($release -and $release.assets) {
    foreach ($candidate in $candidates) {
        $found = $release.assets | Where-Object { $_.name -eq $candidate } | Select-Object -First 1
        if ($found) {
            $selectedCandidate = $found.name
            $downloadUrl = $found.browser_download_url
            break
        }
    }
}

if (-not $downloadUrl) {
    $selectedCandidate = $candidates[0]
    $downloadUrl = "https://github.com/SagerNet/sing-box/releases/download/$tag/$selectedCandidate"
}

$tempRoot = Join-Path $env:TEMP ("carton-singbox-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $selectedCandidate
$extractDir = Join-Path $tempRoot "extract"

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

try {
    Write-Host "Downloading $selectedCandidate from $tag..."
    Invoke-WebRequest -Uri $downloadUrl -Headers $headers -OutFile $archivePath

    Write-Host "Extracting sing-box package..."
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force

    $kernelFile = Get-ChildItem -Path $extractDir -Recurse -File -Filter "sing-box.exe" | Select-Object -First 1
    if (-not $kernelFile) {
        throw "sing-box.exe was not found in downloaded asset: $selectedCandidate"
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
