param(
    [string]$DownloadUrl = "https://github.com/SuRGeoNix/Flyleaf/releases/download/v3.8.11/Flyleaf_v3.8.11_p2.7z",
    [string]$ExpectedSha256 = "5c37f187e3c3e286321273aa84046bd634e34fad65aa7791d0b9da8970e80d4d"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$flyleafToolsPath = Join-Path $repoRoot ".tools/flyleaf"
$runtimePath = Join-Path $flyleafToolsPath "runtime-7.1"
$archivePath = Join-Path $flyleafToolsPath "Flyleaf_v3.8.11_p2.7z"

New-Item -ItemType Directory -Force -Path $flyleafToolsPath | Out-Null

if (!(Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    Write-Host "Downloading Flyleaf's FFmpeg 7.1 runtime bundle."
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $archivePath
}

$actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "Flyleaf runtime SHA256 mismatch. Expected $ExpectedSha256 but found $actualSha256."
}

if (Test-Path -LiteralPath $runtimePath) {
    Remove-Item -LiteralPath $runtimePath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null

tar -xf $archivePath -C $runtimePath "FFmpeg"
if ($LASTEXITCODE -ne 0) {
    throw "Could not extract Flyleaf's FFmpeg runtime. Exit code: $LASTEXITCODE."
}

$requiredLibraries = @(
    "avcodec-61.dll",
    "avdevice-61.dll",
    "avfilter-10.dll",
    "avformat-61.dll",
    "avutil-59.dll",
    "postproc-58.dll",
    "swresample-5.dll",
    "swscale-8.dll"
)

foreach ($library in $requiredLibraries) {
    $libraryPath = Join-Path $runtimePath "FFmpeg/$library"
    if (!(Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
        throw "The Flyleaf runtime is incomplete. Missing: $libraryPath"
    }
}

Write-Host "Flyleaf's FFmpeg runtime is ready at $(Join-Path $runtimePath 'FFmpeg')."
