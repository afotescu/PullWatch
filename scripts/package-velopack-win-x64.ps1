param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumFileName,

    [string]$PackId = "PullWatch.Desktop",
    [string]$PackTitle = "PullWatch",
    [string]$MainExe = "PullWatch.exe",
    [string]$IconPath = "PullWatch.App/Assets/favicon.ico",
    [string]$Runtime = "win-x64"
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

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Resolve-ArtifactPath {
    param(
        [string]$Path,
        [string]$Name
    )

    $resolvedPath = Resolve-RepoPath $Path
    if (!$resolvedPath.StartsWith(
            $artifactsRootWithSeparator,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must resolve under the artifacts directory: $artifactsRoot"
    }

    return $resolvedPath
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsRootWithSeparator = $artifactsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
    throw "Version must look like 0.1.0 or 0.1.0-rc.1. Received: $Version"
}

if ([string]::IsNullOrWhiteSpace($ChecksumFileName) -or
    $ChecksumFileName -ne [System.IO.Path]::GetFileName($ChecksumFileName)) {
    throw "ChecksumFileName must be a file name, not a path."
}

$publishPath = Resolve-ArtifactPath $PublishPath "PublishPath"
$releaseDir = Resolve-ArtifactPath $ReleaseDir "ReleaseDir"
$resolvedIconPath = Resolve-RepoPath $IconPath
$vpkPath = Join-Path $repoRoot ".tools/vpk.exe"

if (!(Test-Path -LiteralPath $publishPath)) {
    throw "Publish path does not exist: $publishPath"
}

if (!(Test-Path -LiteralPath $resolvedIconPath)) {
    throw "Icon path does not exist: $resolvedIconPath"
}

if (!(Test-Path -LiteralPath $vpkPath)) {
    throw "Velopack CLI was not found: $vpkPath"
}

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$vpkArguments = @(
    "pack",
    "--packId",
    $PackId,
    "--packTitle",
    $PackTitle,
    "--packVersion",
    $Version,
    "--packDir",
    $publishPath,
    "--mainExe",
    $MainExe,
    "--outputDir",
    $releaseDir,
    "--icon",
    $resolvedIconPath,
    "--shortcuts",
    "Desktop,StartMenuRoot",
    "--runtime",
    $Runtime,
    "--noPortable"
)

& $vpkPath @vpkArguments
Assert-NativeCommandSucceeded "Velopack pack failed."

$releaseFiles = @(
    Get-ChildItem -LiteralPath $releaseDir -File | Sort-Object Name
)

if ($releaseFiles.Count -eq 0) {
    throw "Velopack did not produce any release files in $releaseDir"
}

$setupFile = Join-Path $releaseDir "$PackId-win-Setup.exe"
if (!(Test-Path -LiteralPath $setupFile)) {
    throw "Velopack did not produce the expected setup executable: $setupFile"
}

$escapedPackId = [regex]::Escape($PackId)
$fullPackages = @(
    $releaseFiles | Where-Object {
        $_.Name -match "^$escapedPackId-.+-full\.nupkg$"
    }
)
if ($fullPackages.Count -ne 1) {
    throw "Velopack should produce exactly one full package, but found $($fullPackages.Count)."
}

$releasesFile = Join-Path $releaseDir "RELEASES"
if (!(Test-Path -LiteralPath $releasesFile)) {
    throw "Velopack did not produce the RELEASES file: $releasesFile"
}

$releasesContent = Get-Content -LiteralPath $releasesFile -Raw
if ([string]::IsNullOrWhiteSpace($releasesContent)) {
    throw "Velopack produced an empty RELEASES file."
}

$portableFiles = @(
    $releaseFiles | Where-Object {
        $_.Name -match "(?i)(portable|\.zip$)"
    }
)
if ($portableFiles.Count -gt 0) {
    $portableNames = $portableFiles.Name -join ", "
    throw "Velopack produced unexpected portable release file(s): $portableNames"
}

$checksumPath = Join-Path $releaseDir $ChecksumFileName
$checksumLines = $releaseFiles | ForEach-Object {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
}
Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ASCII

Write-Host "Packaged Velopack release to $releaseDir"
