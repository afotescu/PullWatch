param(
    [string]$OutputPath = "artifacts/publish/win-x64",
    [string]$Version,
    [string]$FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip",
    [string]$FfmpegSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec",
    [switch]$SkipFfmpegBundle
)

$ErrorActionPreference = "Stop"

function Assert-NativeCommandSucceeded {
    param(
        [string]$FailureMessage
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "PullWatch.App/PullWatch.App.csproj"
$publishPath = Join-Path $repoRoot $OutputPath
$resolvedPublishPath = [System.IO.Path]::GetFullPath($publishPath)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsRootWithSeparator = $artifactsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (!$resolvedPublishPath.StartsWith(
        $artifactsRootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must resolve under the artifacts directory: $artifactsRoot"
}

function Add-FfmpegBundle {
    param(
        [string]$DownloadUrl,
        [string]$ExpectedSha256,
        [string]$ArtifactsRoot,
        [string]$PublishPath
    )

    $downloadDirectory = Join-Path $ArtifactsRoot "downloads"
    $archiveFileName = [System.IO.Path]::GetFileName(([System.Uri]$DownloadUrl).AbsolutePath)
    if ([string]::IsNullOrWhiteSpace($archiveFileName) -or
        !$archiveFileName.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "FFmpeg download URL must point to a zip archive."
    }

    $archivePath = Join-Path $downloadDirectory $archiveFileName
    $extractPath = Join-Path $ArtifactsRoot "ffmpeg-release-essentials"
    $destinationPath = Join-Path $PublishPath "ffmpeg"

    New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null

    if ($ExpectedSha256 -notmatch "^[0-9A-Fa-f]{64}$") {
        throw "Expected FFmpeg SHA256 must be a 64-character hex string."
    }

    Write-Host "Downloading FFmpeg essentials from $DownloadUrl"
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $archivePath

    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    $normalizedExpectedSha256 = $ExpectedSha256.Trim().ToLowerInvariant()
    if ($actualSha256 -ne $normalizedExpectedSha256) {
        throw "FFmpeg archive SHA256 mismatch. Expected $normalizedExpectedSha256 but downloaded $actualSha256."
    }

    if (Test-Path -LiteralPath $extractPath) {
        Remove-Item -LiteralPath $extractPath -Recurse -Force
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force

    $ffmpegExe = Get-ChildItem -LiteralPath $extractPath -Recurse -Filter "ffmpeg.exe" -File |
        Where-Object {
            $parentDirectory = Split-Path -Parent $_.FullName
            [System.StringComparer]::OrdinalIgnoreCase.Equals(
                (Split-Path -Leaf $parentDirectory),
                "bin")
        } |
        Select-Object -First 1

    if (!$ffmpegExe) {
        throw "FFmpeg essentials archive did not contain bin\ffmpeg.exe"
    }

    $sourceBinPath = Split-Path -Parent $ffmpegExe.FullName

    if (Test-Path -LiteralPath $destinationPath) {
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null

    Copy-Item -LiteralPath (Join-Path $sourceBinPath "ffmpeg.exe") -Destination $destinationPath

    Get-ChildItem -LiteralPath $sourceBinPath -Filter "*.dll" -File |
        Copy-Item -Destination $destinationPath

    $sourceRootPath = Split-Path -Parent $sourceBinPath
    foreach ($noticeFileName in @("LICENSE", "LICENSE.txt", "README.txt", "README.md")) {
        $noticeFilePath = Join-Path $sourceRootPath $noticeFileName
        if (Test-Path -LiteralPath $noticeFilePath) {
            $extension = [System.IO.Path]::GetExtension($noticeFileName)
            $destinationFileName = if ([string]::IsNullOrEmpty($extension)) {
                "FFmpeg-$noticeFileName.txt"
            } else {
                "FFmpeg-$noticeFileName"
            }
            Copy-Item -LiteralPath $noticeFilePath -Destination (Join-Path $destinationPath $destinationFileName)
        }
    }

    $bundledFfmpegPath = Join-Path $destinationPath "ffmpeg.exe"
    $ffmpegVersion = & $bundledFfmpegPath -version 2>&1
    Assert-NativeCommandSucceeded "Bundled ffmpeg.exe could not run."

    $ffmpegFilters = & $bundledFfmpegPath -hide_banner -filters 2>&1
    Assert-NativeCommandSucceeded "Bundled ffmpeg.exe could not list filters."

    if (!($ffmpegFilters | Select-String -SimpleMatch "gfxcapture")) {
        throw "Bundled ffmpeg.exe does not include the required gfxcapture filter."
    }

    Write-Host "Bundled $($ffmpegVersion | Select-Object -First 1)"
}

$lockingProcesses = Get-Process |
    Where-Object {
        try {
            $_.Path -and
            [System.IO.Path]::GetFullPath($_.Path).StartsWith(
                $resolvedPublishPath,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    } |
    Select-Object Id, ProcessName, Path

if ($lockingProcesses) {
    Write-Host "Close these running processes before publishing:"
    $lockingProcesses | Format-Table -AutoSize
    throw "The publish directory is in use: $resolvedPublishPath"
}

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

dotnet clean $projectPath `
    -c Release `
    -p:Platform=x64
Assert-NativeCommandSucceeded "dotnet clean failed."

$publishProperties = @("-p:PublishProfile=win-x64")

if (![string]::IsNullOrWhiteSpace($Version)) {
    $publishProperties += "-p:Version=$Version"
    $publishProperties += "-p:InformationalVersion=$Version"
}

dotnet publish $projectPath @publishProperties -o $publishPath
Assert-NativeCommandSucceeded "dotnet publish failed."

$expectedExe = Join-Path $publishPath "PullWatch.exe"
$oldExe = Join-Path $publishPath "PullWatch.App.exe"

if (!(Test-Path -LiteralPath $expectedExe)) {
    throw "Publish did not produce the expected executable: $expectedExe"
}

if (Test-Path -LiteralPath $oldExe) {
    throw "Stale executable found in publish output: $oldExe"
}

if ($SkipFfmpegBundle) {
    Write-Host "Skipping FFmpeg bundle."
} else {
    Add-FfmpegBundle `
        -DownloadUrl $FfmpegDownloadUrl `
        -ExpectedSha256 $FfmpegSha256 `
        -ArtifactsRoot $artifactsRoot `
        -PublishPath $publishPath
}

$readmePath = Join-Path $publishPath "README.txt"
@"
PullWatch release build

Run PullWatch.exe. Keep the ffmpeg folder next to PullWatch.exe.

Screen recording requires:
- Windows x64

Automatic recording requires World of Warcraft combat logging to be enabled.
Start PullWatch before the Mythic+ key or raid pull so it can see the combat-log
start event.

Closing the PullWatch window hides it to the system tray. Use Exit from the tray
icon menu to fully quit the app.

See LICENSE.txt and PRIVACY.md in this folder for license and privacy details.
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $publishPath "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $repoRoot "PRIVACY.md") -Destination (Join-Path $publishPath "PRIVACY.md")

$totalBytes = (Get-ChildItem -LiteralPath $publishPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalMiB = [Math]::Round($totalBytes / 1MB, 2)

Write-Host "Published to $publishPath"
Write-Host "Executable: $expectedExe"
Write-Host "Total size: $totalMiB MiB ($totalBytes bytes)"
